using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase12RegionSchedulingTests
{
    [Fact]
    public void RegionContract_IsPinnedDefaultOffAndCapabilityGated()
    {
        HybridCpuRegionSchedulingContractV1 contract = HybridCpuRegionSchedulingContractV1.Default;
        Assert.Equal("hybridcpu.region-scheduling/v1", HybridCpuRegionSchedulingContractV1.SchemaId);
        Assert.Equal(IrRegionSchedulingModeV1.Disabled, contract.DefaultMode);
        Assert.Equal(IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly, contract.OutputDisposition);
        Assert.Equal(64, HybridCpuRegionSchedulingContractV1.DefaultMaximumCandidates);
        Assert.Equal("06516900d484c5eda6d72a35d393373da5322fd16693485841221bba64b4afee", contract.ContractDigest);
        Assert.Equal("0a39a6ed13d333046c1278621859cef8aaf15021f434b5fba14ec67be4be0ec1", contract.OptionsDigest);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.Capabilities.TargetContractDigest);
        Assert.Equal(IrRegionMotionSupportV1.SupportedStraightLine, contract.Capabilities.StraightLineFallthrough);
        Assert.Equal(IrRegionMotionSupportV1.Unsupported, contract.Capabilities.GuardedPredication);
        Assert.Equal(IrRegionMotionSupportV1.Unknown, contract.Capabilities.SpeculativeFaultSuppression);
        Assert.Equal(IrRegionMotionSupportV1.Unsupported, contract.Capabilities.Hyperblocks);
    }

    [Fact]
    public void DisabledMode_PreservesExactBasicBlockBytesAndOnlyFormsWitnesses()
    {
        HybridCpuInstructionWord[] words = IndependentWords();
        IrProgram program = BuildTwoBlockProgram(words);
        IrRegionSchedulingResultV1 result = new HybridCpuRegionSchedulerV1().Evaluate(program);

        Assert.Equal(IrRegionEvaluationStatusV1.DisabledFallback, result.Status);
        Assert.Null(result.SelectedCandidate);
        Assert.Null(result.ShadowSchedule);
        Assert.Equal(IrRegionFactDispositionV1.FallbackFactsUnchanged, result.FactDisposition);
        Assert.Equal(IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly, result.OutputDisposition);
        Assert.Equal(IrRegionCandidateStatusV1.QualifiedStraightLineEbb, Assert.Single(result.Candidates).Status);
        byte[] fallback = new HybridCpuBundleSerializer().SerializeProgram(
            new HybridCpuBundleLowerer().LowerProgram(result.FallbackBundles));
        Assert.Equal(HybridCpuCanonicalCompiler.CompileProgram(
            0,
            words,
            labelDeclarations: [new("second", 1)]).ProgramImage, fallback);
    }

    [Fact]
    public void StraightLineShadow_RecomputesFactsUsesExactPlacementAndShowsDeterministicBenefit()
    {
        IrProgram program = BuildTwoBlockProgram(IndependentWords());
        HybridCpuRegionSchedulerV1 scheduler = new HybridCpuRegionSchedulerV1();
        IrRegionSchedulingResultV1 first = scheduler.Evaluate(program, HybridCpuRegionSchedulingOptionsV1.Shadow);
        IrRegionSchedulingResultV1 second = scheduler.Evaluate(program, HybridCpuRegionSchedulingOptionsV1.Shadow);

        Assert.Equal(IrRegionEvaluationStatusV1.ShadowQualified, first.Status);
        Assert.Equal(2, first.FallbackCycles);
        Assert.Equal(1, first.ShadowCycles);
        Assert.Equal(first.SelectedCandidate!.RegionId, second.SelectedCandidate!.RegionId);
        Assert.Equal(
            IrRegionFactDispositionV1.DependencyLivenessPressureResourceAndPlacementRecomputedMiiInvalidated,
            first.FactDisposition);
        IrProgram shadowProgram = Assert.IsType<IrProgramSchedule>(first.ShadowSchedule).Program;
        Assert.True(shadowProgram.Contract.DerivedFacts.IsCurrent(shadowProgram.Contract.DerivedFacts.Dependency));
        Assert.True(shadowProgram.Contract.DerivedFacts.IsCurrent(shadowProgram.Contract.DerivedFacts.Liveness));
        Assert.True(shadowProgram.Contract.DerivedFacts.IsCurrent(shadowProgram.Contract.DerivedFacts.Pressure));
        Assert.Null(shadowProgram.Contract.DerivedFacts.Mii);
        Assert.Null(shadowProgram.Contract.DerivedFacts.Placement);

        IrProgramBundlingResult bundles = Assert.IsType<IrProgramBundlingResult>(first.ShadowBundles);
        IrMaterializedBundle bundle = Assert.Single(Assert.Single(bundles.BlockResults).Bundles);
        Assert.True(bundle.SlotAssignment.HasStructuralPlacement);
        Assert.Equal(
            program.Instructions.Select(static instruction => instruction.Index).OrderBy(static index => index),
            bundle.CycleGroup.Instructions.Select(static instruction => instruction.Index).OrderBy(static index => index));
        Assert.Equal(
            Project(first.ShadowSchedule!),
            Project(second.ShadowSchedule!));
    }

    [Fact]
    public void ProfileCannotLegalizeCandidateOrChangeQualifiedShadowSchedule()
    {
        IrProgram legal = BuildTwoBlockProgram(IndependentWords());
        IrRegionSchedulingResultV1 noProfile = new HybridCpuRegionSchedulerV1().Evaluate(
            legal,
            HybridCpuRegionSchedulingOptionsV1.Shadow);
        IrRegionSchedulingResultV1 extremeProfile = new HybridCpuRegionSchedulerV1().Evaluate(
            legal,
            HybridCpuRegionSchedulingOptionsV1.Shadow with { ProfileDigest = new string('f', 64) });
        Assert.Equal(Project(noProfile.ShadowSchedule!), Project(extremeProfile.ShadowSchedule!));
        Assert.Equal(noProfile.SelectedCandidate!.Status, extremeProfile.SelectedCandidate!.Status);
        Assert.NotEqual(noProfile.OptionsDigest, extremeProfile.OptionsDigest);
        Assert.Equal(new string('f', 64), extremeProfile.ProfileIdentity);

        HybridCpuInstructionWord[] predicatedWords = IndependentWords();
        predicatedWords[1].PredicateMask = 1;
        IrRegionSchedulingResultV1 predicated = new HybridCpuRegionSchedulerV1().Evaluate(
            BuildTwoBlockProgram(predicatedWords),
            HybridCpuRegionSchedulingOptionsV1.Shadow with { ProfileDigest = new string('a', 64) });
        Assert.Equal(IrRegionEvaluationStatusV1.NoQualifiedCandidate, predicated.Status);
        Assert.Equal(IrRegionCandidateStatusV1.RejectedPredicate, Assert.Single(predicated.Candidates).Status);
    }

    [Fact]
    public void MemoryLattice_IsExplicitAndConservative()
    {
        IrCanonicalMemoryEffectV1 write = Memory(
            IrMemoryEffectKind.Write,
            new IrMemoryRegion(0x1000, 8, true));
        IrCanonicalMemoryEffectV1 same = Memory(
            IrMemoryEffectKind.Read,
            new IrMemoryRegion(0x1000, 8, false));
        IrCanonicalMemoryEffectV1 overlap = Memory(
            IrMemoryEffectKind.Read,
            new IrMemoryRegion(0x1004, 8, false));
        IrCanonicalMemoryEffectV1 disjoint = Memory(
            IrMemoryEffectKind.Read,
            new IrMemoryRegion(0x2000, 8, false));

        Assert.Equal(IrRegionMemoryRelationV1.MustAlias,
            HybridCpuRegionSchedulerV1.ClassifyMemoryRelation(write, same));
        Assert.Equal(IrRegionMemoryRelationV1.MayAlias,
            HybridCpuRegionSchedulerV1.ClassifyMemoryRelation(write, overlap));
        Assert.Equal(IrRegionMemoryRelationV1.NoAlias,
            HybridCpuRegionSchedulerV1.ClassifyMemoryRelation(write, disjoint));
        Assert.Equal(IrRegionMemoryRelationV1.Unknown,
            HybridCpuRegionSchedulerV1.ClassifyMemoryRelation(write, IrCanonicalMemoryEffectV1.Unknown));
    }

    [Fact]
    public void FaultingMemoryAndSpecialContours_AreRejectedBeforeMotion()
    {
        HybridCpuInstructionWord[] loadWords = IndependentWords();
        loadWords[1].OpCode = (uint)HybridCpuOpcode.LW;
        IrRegionSchedulingResultV1 load = new HybridCpuRegionSchedulerV1().Evaluate(
            BuildTwoBlockProgram(loadWords), HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(IrRegionEvaluationStatusV1.NoQualifiedCandidate, load.Status);
        Assert.Contains(
            Assert.Single(load.Candidates).Status,
            new[]
            {
                IrRegionCandidateStatusV1.RejectedMemory,
                IrRegionCandidateStatusV1.RejectedFaultOrArchitecturalEffect
            });

        HybridCpuInstructionWord[] systemWords = IndependentWords();
        systemWords[1].OpCode = (uint)HybridCpuOpcode.FENCE;
        IrRegionSchedulingResultV1 system = new HybridCpuRegionSchedulerV1().Evaluate(
            BuildTwoBlockProgram(systemWords), HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(IrRegionCandidateStatusV1.RejectedSpecialContour, Assert.Single(system.Candidates).Status);

        IrProgram lane6Program = ReplaceInstruction(
            BuildTwoBlockProgram(IndependentWords()),
            1,
            instruction => instruction with
            {
                Annotation = instruction.Annotation with { RequiredSlotClass = IrSlotClass.DmaStreamClass }
            });
        IrRegionSchedulingResultV1 lane6 = new HybridCpuRegionSchedulerV1().Evaluate(
            lane6Program, HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(IrRegionCandidateStatusV1.RejectedSpecialContour, Assert.Single(lane6.Candidates).Status);

        IrProgram atomicProgram = ReplaceInstruction(
            BuildTwoBlockProgram(IndependentWords()),
            1,
            instruction => instruction with
            {
                SideEffects = new(
                    new(
                        IrMemoryEffectKind.Read | IrMemoryEffectKind.Write | IrMemoryEffectKind.Atomic,
                        IrAddressSpaceIdentity.Generic,
                        IrMemoryOrdering.SequentiallyConsistent,
                        new(0x1000, 8, false),
                        new(0x1000, 8, true)),
                    IrArchitecturalEffectKind.None)
            });
        IrRegionSchedulingResultV1 atomic = new HybridCpuRegionSchedulerV1().Evaluate(
            atomicProgram, HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(IrRegionCandidateStatusV1.RejectedSpecialContour, Assert.Single(atomic.Candidates).Status);

        IrProgram volatileProgram = ReplaceInstruction(
            BuildTwoBlockProgram(IndependentWords()),
            1,
            instruction => instruction with
            {
                SideEffects = new(
                    new(
                        IrMemoryEffectKind.Write | IrMemoryEffectKind.Volatile,
                        IrAddressSpaceIdentity.Device,
                        IrMemoryOrdering.Release,
                        null,
                        new(0x3000, 8, true)),
                    IrArchitecturalEffectKind.None)
            });
        IrRegionSchedulingResultV1 volatileResult = new HybridCpuRegionSchedulerV1().Evaluate(
            volatileProgram, HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(
            IrRegionCandidateStatusV1.RejectedSpecialContour,
            Assert.Single(volatileResult.Candidates).Status);

        IrProgram mustAliasProgram = ReplaceInstruction(
            ReplaceInstruction(
                BuildTwoBlockProgram(IndependentWords()),
                0,
                instruction => instruction with
                {
                    SideEffects = new(
                        Memory(IrMemoryEffectKind.Write, new(0x4000, 8, true)),
                        IrArchitecturalEffectKind.None)
                }),
            1,
            instruction => instruction with
            {
                SideEffects = new(
                    Memory(IrMemoryEffectKind.Read, new(0x4000, 8, false)),
                    IrArchitecturalEffectKind.None)
            });
        IrRegionCandidateV1 mustAlias = Assert.Single(new HybridCpuRegionSchedulerV1().Evaluate(
            mustAliasProgram, HybridCpuRegionSchedulingOptionsV1.Shadow).Candidates);
        Assert.Equal(IrRegionCandidateStatusV1.RejectedMemory, mustAlias.Status);
        Assert.Equal(IrRegionMemoryRelationV1.MustAlias, Assert.Single(mustAlias.MemoryWitnesses).Relation);
    }

    [Fact]
    public void SideExit_IsRejectedAndCannotBeLegalizedByProfile()
    {
        HybridCpuInstructionWord[] words =
        [
            Word(HybridCpuOpcode.BEQ, 30, 1, 2),
            Word(HybridCpuOpcode.ADD, 3, 4, 5),
            Word(HybridCpuOpcode.ADD, 6, 7, 8)
        ];
        IrProgram program = new HybridCpuIrBuilder().BuildProgram(
            0,
            words,
            labelDeclarations: [new("fallthrough", 1), new("target", 2)],
            controlFlowTargetReferences: [new(0, "target", IrControlTransferKind.ConditionalBranch)]);
        IrRegionSchedulingResultV1 result = new HybridCpuRegionSchedulerV1().Evaluate(
            program,
            HybridCpuRegionSchedulingOptionsV1.Shadow with { ProfileDigest = new string('9', 64) });

        Assert.Equal(IrRegionEvaluationStatusV1.NoQualifiedCandidate, result.Status);
        Assert.Contains(result.Candidates, static candidate =>
            candidate.Status == IrRegionCandidateStatusV1.RejectedSideExit);
        Assert.Null(result.ShadowSchedule);

        IrProgram callProgram = new HybridCpuIrBuilder().BuildProgram(
            0,
            [Word(HybridCpuOpcode.JAL, 1, 2, 3), Word(HybridCpuOpcode.ADD, 4, 5, 6)],
            labelDeclarations: [new("callee", 1)],
            controlFlowTargetReferences: [new(0, "callee", IrControlTransferKind.Call)]);
        IrRegionSchedulingResultV1 call = new HybridCpuRegionSchedulerV1().Evaluate(
            callProgram, HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(IrRegionCandidateStatusV1.RejectedControlEdge, Assert.Single(call.Candidates).Status);
    }

    [Fact]
    public void MutatedProgram_RejectsStaleRegionWitnessAndReusesFallback()
    {
        IrProgram program = BuildTwoBlockProgram(IndependentWords());
        HybridCpuRegionSchedulerV1 scheduler = new HybridCpuRegionSchedulerV1();
        IrRegionCandidateV1 candidate = Assert.Single(scheduler.Evaluate(program).Candidates);
        IrProgram mutated = program with
        {
            Contract = program.Contract with
            {
                DerivedFacts = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation()
            }
        };

        IrRegionSchedulingResultV1 result = scheduler.EvaluateCandidate(
            mutated,
            candidate,
            HybridCpuRegionSchedulingOptionsV1.Shadow);
        Assert.Equal(IrRegionEvaluationStatusV1.StaleWitness, result.Status);
        Assert.Equal(IrRegionFactDispositionV1.StaleFactsRejected, result.FactDisposition);
        Assert.Equal("HCRG0003", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.ShadowSchedule);
    }

    [Fact]
    public void CandidateBudget_IsDeterministicAndFailClosed()
    {
        IrProgram program = BuildTwoBlockProgram(IndependentWords());
        HybridCpuRegionSchedulerV1 scheduler = new HybridCpuRegionSchedulerV1();
        IrRegionSchedulingResultV1 zero = scheduler.Evaluate(
            program,
            new(IrRegionSchedulingModeV1.Shadow, 0, null));
        IrRegionSchedulingResultV1 excessive = scheduler.Evaluate(
            program,
            new(IrRegionSchedulingModeV1.Shadow, 257, null));

        Assert.Equal(IrRegionEvaluationStatusV1.InvalidOptions, zero.Status);
        Assert.Equal("HCRG0001", Assert.Single(zero.Diagnostics).Code);
        Assert.Equal(IrRegionEvaluationStatusV1.InvalidOptions, excessive.Status);
        Assert.Empty(excessive.Candidates);

        IrRegionSchedulingResultV1 malformedProfile = scheduler.Evaluate(
            program,
            HybridCpuRegionSchedulingOptionsV1.Shadow with { ProfileDigest = "not-a-digest" });
        Assert.Equal(IrRegionEvaluationStatusV1.InvalidOptions, malformedProfile.Status);
        Assert.Equal("HCRG0005", Assert.Single(malformedProfile.Diagnostics).Code);
    }

    [Fact]
    public void GeneratedIndependentKernels_HaveFallbackEquivalentShadowSemantics()
    {
        for (byte seed = 1; seed <= 28; seed++)
        {
            HybridCpuInstructionWord[] words =
            [
                Word(HybridCpuOpcode.ADD, 20, seed, (byte)(seed + 1)),
                Word(HybridCpuOpcode.ADD, 21, (byte)(seed + 2), (byte)(seed + 3))
            ];
            IrRegionSchedulingResultV1 result = new HybridCpuRegionSchedulerV1().Evaluate(
                BuildTwoBlockProgram(words), HybridCpuRegionSchedulingOptionsV1.Shadow);
            Assert.Equal(IrRegionEvaluationStatusV1.ShadowQualified, result.Status);
            Assert.Equal(Execute(result.FallbackSchedule, seed), Execute(result.ShadowSchedule!, seed));
        }
    }

    private static IrProgram BuildTwoBlockProgram(HybridCpuInstructionWord[] words) =>
        new HybridCpuIrBuilder().BuildProgram(0, words, labelDeclarations: [new("second", 1)]);

    private static HybridCpuInstructionWord[] IndependentWords() =>
    [
        Word(HybridCpuOpcode.ADD, 1, 2, 3),
        Word(HybridCpuOpcode.ADD, 4, 5, 6)
    ];

    private static HybridCpuInstructionWord Word(HybridCpuOpcode opcode, byte destination, byte source1, byte source2) => new()
    {
        OpCode = (uint)opcode,
        DataTypeValue = HybridCpuDataType.INT32,
        PredicateMask = byte.MaxValue,
        Word1 = HybridCpuInstructionWord.PackArchRegs(destination, source1, source2),
        VirtualThreadId = 0
    };

    private static IrCanonicalMemoryEffectV1 Memory(IrMemoryEffectKind kind, IrMemoryRegion region) => new(
        kind,
        IrAddressSpaceIdentity.Generic,
        IrMemoryOrdering.NotAtomic,
        kind.HasFlag(IrMemoryEffectKind.Read) ? region : null,
        kind.HasFlag(IrMemoryEffectKind.Write) ? region : null);

    private static IrProgram ReplaceInstruction(
        IrProgram program,
        int instructionIndex,
        Func<IrInstruction, IrInstruction> transform)
    {
        IrInstruction replacement = transform(program.Instructions[instructionIndex]);
        IrInstruction[] instructions = program.Instructions
            .Select(instruction => instruction.Index == instructionIndex ? replacement : instruction)
            .ToArray();
        IrBasicBlock[] blocks = program.BasicBlocks.Select(block => block with
        {
            Instructions = block.Instructions
                .Select(instruction => instruction.Index == instructionIndex ? replacement : instruction)
                .ToArray()
        }).ToArray();
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = program.ControlFlowGraph with { Blocks = blocks }
        };
    }

    private static IReadOnlyDictionary<int, long> Execute(IrProgramSchedule schedule, byte seed)
    {
        Dictionary<int, long> registers = Enumerable.Range(0, 32).ToDictionary(static index => index, index => (long)(index + seed));
        foreach (IrScheduledInstruction scheduled in schedule.BlockSchedules
                     .SelectMany(static block => block.ScheduledInstructions)
                     .OrderBy(static item => item.Cycle)
                     .ThenBy(static item => item.OrderInCycle))
        {
            IrInstruction instruction = scheduled.Instruction;
            Assert.Equal(HybridCpuOpcode.ADD, instruction.Opcode);
            int destination = checked((int)Assert.Single(instruction.Annotation.Defs).Value);
            IrOperand[] uses = instruction.Annotation.Uses.ToArray();
            registers[destination] = registers[checked((int)uses[0].Value)] + registers[checked((int)uses[1].Value)];
        }
        return registers;
    }

    private static string Project(IrProgramSchedule schedule) => string.Join('|',
        schedule.BlockSchedules.SelectMany(static block => block.ScheduledInstructions)
            .OrderBy(static item => item.Cycle)
            .ThenBy(static item => item.OrderInCycle)
            .Select(static item => $"{item.InstructionIndex}:{item.Cycle}:{item.OrderInCycle}"));
}
