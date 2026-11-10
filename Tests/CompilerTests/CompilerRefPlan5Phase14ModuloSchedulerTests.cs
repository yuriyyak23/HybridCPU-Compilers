using System.Reflection;
using System.Globalization;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase14ModuloSchedulerTests
{
    [Fact]
    public void Contract_IsPinnedBoundedSolverNeutralAndWitnessOnly()
    {
        HybridCpuModuloSchedulingContractV1 contract = HybridCpuModuloSchedulingContractV1.Default;
        Assert.Equal("hybridcpu.modulo-schedule-witness/v1", HybridCpuModuloSchedulingContractV1.SchemaId);
        Assert.Equal("7862722df6e71c05f9541de0b73efd7f6261fcb7e5c6c2eafc229faaf2b9cd99",
            contract.ContractDigest);
        Assert.Equal("80484f83dabe862ac0956fee2c53327507649d91cb58e6be44676e3d1ce05bf4",
            contract.ProductionOptionsDigest);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.TargetDigest);
        Assert.Equal(HybridCpuLoopMiiContractV1.Default.ContractDigest, contract.LoopMiiContractDigest);
        Assert.Equal(HybridCpuModuloSchedulerOptionsV1.Production.OptionsDigest, contract.ProductionOptionsDigest);
        Assert.False(contract.DefaultEnabled);
        Assert.Equal(IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly, contract.OutputDisposition);
        Assert.Equal(new IrModuloScheduleBudgetsV1(16, 65_536, 131_072, 16_384, 256, 262_144, 64),
            HybridCpuModuloSchedulerOptionsV1.Production.Budgets);
    }

    [Fact]
    public void QualifiedLoop_ProducesIndependentlyValidatedTemporalSlotAndResourceWitness()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 3);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        IrModuloScheduleResultV1 result = scheduler.Schedule(program, loop, mii, model);

        Assert.True(result.Status == IrModuloScheduleStatusV1.Feasible,
            $"status={result.Status}; diagnostics={string.Join('|', result.Diagnostics.Select(static item => item.Message))}; attempts={string.Join('|', result.Attempts.Select(static item => $"{item.InitiationInterval}:{item.Status}:{item.CandidateStates}:{item.ResourceCuts}:{item.ExactPlacementStates}"))}");
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(result.Witness);
        Assert.Equal(result.ChosenIi, witness.InitiationInterval);
        Assert.True(witness.InitiationInterval >= mii.ProvenLowerBoundIi);
        Assert.Equal(loop.Instructions.Count, witness.Operations.Count);
        Assert.All(witness.Operations, operation =>
            Assert.Equal(operation.Cycle % witness.InitiationInterval, operation.ModuloCycle));
        Assert.Equal(loop.DistanceDependencies.Select(static edge => edge.EdgeId).Order(),
            witness.DependenceProofRefs.Order());
        Assert.NotEmpty(witness.MiiProofRefs);
        Assert.All(witness.ResourceReservations, static reservation => Assert.InRange(reservation.IssueCount, 1, 8));
        Assert.Contains(witness.ResourceReservations, static reservation =>
            reservation.RegisterGroupReservations.Count > 0);
        Assert.True(scheduler.ValidateWitness(program, loop, mii, witness, model).IsValid);
    }

    [Fact]
    public void ReorderedCanonicalContainers_ProduceIdenticalChosenIiAndWitness()
    {
        IrProgram firstProgram = BuildLoopProgram(loopInstructionCount: 4);
        IrProgram secondProgram = firstProgram with
        {
            ControlFlowGraph = firstProgram.ControlFlowGraph with
            {
                Blocks = firstProgram.BasicBlocks.Reverse().ToArray(),
                Edges = firstProgram.ControlFlowGraph.Edges.Reverse().ToArray()
            },
            ValueFlow = firstProgram.ValueFlow with
            {
                Values = firstProgram.ValueFlow.Values.Reverse().ToArray(),
                Accesses = firstProgram.ValueFlow.Accesses.Reverse().ToArray()
            }
        };
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 firstLoop, IrLoopMiiReportV1 firstMii) = Analyze(firstProgram, model);
        (IrCanonicalLoopV1 secondLoop, IrLoopMiiReportV1 secondMii) = Analyze(secondProgram, model);
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        IrModuloScheduleResultV1 first;
        IrModuloScheduleResultV1 second;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            first = scheduler.Schedule(firstProgram, firstLoop, firstMii, model);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            second = scheduler.Schedule(secondProgram, secondLoop, secondMii, model);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Equal(IrModuloScheduleStatusV1.Feasible, first.Status);
        Assert.Equal(first.ChosenIi, second.ChosenIi);
        Assert.Equal(first.Witness!.WitnessDigest, second.Witness!.WitnessDigest);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
    }

    [Fact]
    public void ExactW8Counterexample_RaisesIiAndRecordsPlacementNotTemporalFailure()
    {
        IrProgram program = TransformLoopInstructions(BuildLoopProgram(loopInstructionCount: 3, noOperands: true), instruction =>
            instruction with
            {
#pragma warning disable CS0618
                Annotation = instruction.Annotation with { LegalSlots = IrIssueSlotMask.Slot0 }
#pragma warning restore CS0618
            });
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        IrModuloScheduleResultV1 result = new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model);

        Assert.True(result.Status == IrModuloScheduleStatusV1.Feasible,
            $"status={result.Status}; diagnostics={string.Join('|', result.Diagnostics.Select(static item => item.Message))}; attempts={string.Join('|', result.Attempts.Select(static item => $"{item.InitiationInterval}:{item.Status}:{item.CandidateStates}:{item.ResourceCuts}:{item.ExactPlacementStates}"))}");
        Assert.True(result.ChosenIi > mii.ProvenLowerBoundIi);
        Assert.Contains(result.Attempts, static attempt =>
            attempt.Status == IrModuloScheduleStatusV1.ExactPlacementInfeasible &&
            attempt.BindingFacts.Any(binding => binding.Contains("shared-exact-w8-search", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Attempts, static attempt =>
            attempt.Status == IrModuloScheduleStatusV1.TemporalInfeasible);
    }

    [Fact]
    public void MultiDistanceTemporalSolver_UsesLatencyMinusDistanceTimesIiAndClassifiesPositiveCycles()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, _) = Analyze(program, model);
        IrCanonicalLoopV1 feasible = loop with
        {
            DistanceDependencies =
            [
                new("a", 1, 2, 0, 3, IrInstructionDependencyKind.RegisterRaw,
                    IrLoopProofPrecisionV1.Exact, "forward"),
                new("b", 2, 1, 2, 4, IrInstructionDependencyKind.RegisterRaw,
                    IrLoopProofPrecisionV1.Exact, "distance-two")
            ]
        };
        Assert.Equal(IrModuloScheduleStatusV1.Feasible, InvokeTemporalStatus(feasible, ii: 4));
        IrCanonicalLoopV1 infeasible = feasible with
        {
            DistanceDependencies =
            [
                new("a", 1, 2, 0, 3, IrInstructionDependencyKind.RegisterRaw,
                    IrLoopProofPrecisionV1.Exact, "forward"),
                new("b", 2, 1, 0, 4, IrInstructionDependencyKind.RegisterRaw,
                    IrLoopProofPrecisionV1.Exact, "zero-distance-cycle")
            ]
        };
        Assert.Equal(IrModuloScheduleStatusV1.TemporalInfeasible, InvokeTemporalStatus(infeasible, ii: 4));
    }

    [Fact]
    public void ExactMemoryLaneAndCertificateReservations_AreModuloCycleWitnessFacts()
    {
        IrProgram memory = TransformLoopInstructions(BuildLoopProgram(loopInstructionCount: 2), instruction => instruction with
        {
            Annotation = instruction.Annotation with { MemoryReadRegion = new(0x1000, 4, false) },
            SideEffects = new(
                new(IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic,
                    new(0x1000, 4, false), null),
                IrArchitecturalEffectKind.None)
        });
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(memory, model);
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(
            new HybridCpuModuloSchedulerV1().Schedule(memory, loop, mii, model).Witness);
        Assert.All(witness.ResourceReservations, static reservation =>
        {
            Assert.True(reservation.BankReservations.Count <= 1);
            Assert.True(reservation.ChannelReservations.Count <= 1);
        });

        IrProgram lane = TransformLoopInstructions(BuildLoopProgram(), (instruction, ordinal) => instruction with
        {
#pragma warning disable CS0618
            Annotation = instruction.Annotation with
            {
                RequiredSlotClass = ordinal == 0 ? IrSlotClass.DmaStreamClass : IrSlotClass.SystemSingleton,
                LegalSlots = ordinal == 0 ? IrIssueSlotMask.Slot6 : IrIssueSlotMask.Slot7
            }
#pragma warning restore CS0618
        });
        (IrCanonicalLoopV1 laneLoop, IrLoopMiiReportV1 laneMii) = Analyze(lane, model);
        IrModuloScheduleWitnessV1 laneWitness = Assert.IsType<IrModuloScheduleWitnessV1>(
            new HybridCpuModuloSchedulerV1().Schedule(lane, laneLoop, laneMii, model).Witness);
        Assert.Equal(1, laneWitness.ResourceReservations.Sum(static reservation => reservation.Lane6Reservations));
        Assert.Equal(1, laneWitness.ResourceReservations.Sum(static reservation => reservation.Lane7Reservations));
        Assert.Equal(2, laneWitness.ResourceReservations.Sum(static reservation => reservation.StructuralCertificateReservations));
    }

    [Fact]
    public void CandidateBudgetExhaustion_IsDeterministicAndNeverReportedAsUnsat()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 4);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuModuloSchedulerOptionsV1 options = HybridCpuModuloSchedulerOptionsV1.Create(
            new(2, 128, 1, 8, 2, 64, 4));
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        IrModuloScheduleResultV1 first = scheduler.Schedule(program, loop, mii, model, options);
        IrModuloScheduleResultV1 second = scheduler.Schedule(program, loop, mii, model, options);

        Assert.Equal(IrModuloScheduleStatusV1.BudgetExhausted, first.Status);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
        Assert.Contains(first.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("not an infeasibility proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(first.Diagnostics, static diagnostic =>
            diagnostic.Message.Contains("UNSAT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProfileCannotChangeScheduleAndStaleOrUnknownProofsFailClosed()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        IrLoopMiiReportV1 absent = analyzer.ComputeMii(program, loop, model);
        IrLoopMiiReportV1 profiled = analyzer.ComputeMii(program, loop, model, new string('b', 64));
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        IrModuloScheduleResultV1 first = scheduler.Schedule(program, loop, absent, model);
        IrModuloScheduleResultV1 second = scheduler.Schedule(program, loop, profiled, model);
        Assert.Equal(first.Witness!.WitnessDigest, second.Witness!.WitnessDigest);

        Assert.Equal(IrModuloScheduleStatusV1.StaleProof,
            scheduler.Schedule(program, loop with { VersionStamp = new string('0', 64) }, absent, model).Status);
        IrLoopMiiReportV1 unknown = absent with { Eligibility = IrLoopMiiEligibilityV1.IneligibleUnknown };
        Assert.Equal(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact,
            scheduler.Schedule(program, loop, unknown, model).Status);
        HybridCpuModuloSchedulerOptionsV1 forgedOptions = HybridCpuModuloSchedulerOptionsV1.Production with
        {
            OptionsDigest = new string('f', 64)
        };
        Assert.Equal(IrModuloScheduleStatusV1.UnsupportedSemanticOrResourceFact,
            scheduler.Schedule(program, loop, absent, model, forgedOptions).Status);
    }

    [Fact]
    public void WitnessTamperingWithCycleSlotOrProofReference_IsRejected()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 3);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuModuloSchedulerV1 scheduler = new HybridCpuModuloSchedulerV1();
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(
            scheduler.Schedule(program, loop, mii, model).Witness);
        IrModuloScheduledOperationV1 first = witness.Operations[0];
        IrModuloScheduleWitnessV1 badCycle = witness with
        {
            Operations = [first with { Cycle = first.Cycle + witness.InitiationInterval }, .. witness.Operations.Skip(1)]
        };
        Assert.False(scheduler.ValidateWitness(program, loop, mii, badCycle, model).IsValid);
        IrModuloScheduleWitnessV1 badSlot = witness with
        {
            Operations = [first with { IssueSlot = 99 }, .. witness.Operations.Skip(1)]
        };
        Assert.False(scheduler.ValidateWitness(program, loop, mii, badSlot, model).IsValid);
        IrModuloScheduleWitnessV1 badProof = witness with { DependenceProofRefs = ["forged"] };
        Assert.Equal(IrModuloScheduleStatusV1.StaleProof,
            scheduler.ValidateWitness(program, loop, mii, badProof, model).Status);
    }

    [Fact]
    public void LifetimeSummary_UsesPhase07DigestAndHasNoPhysicalAllocationAuthority()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 4);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(
            new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model).Witness);

        Assert.Equal(loop.ValueAnalysis.ValueFlowDigest, witness.LifetimePressure.ValueFlowDigest);
        Assert.Equal(IrLoopProofPrecisionV1.Exact, witness.LifetimePressure.Precision);
        Assert.True(witness.LifetimePressure.CapacityAtChosenIi >=
            witness.LifetimePressure.PeakRegisterGroupPressure);
        Assert.DoesNotContain("physical", witness.LifetimePressure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalysisAndKernelWitnessNeverChangeEmittedNativeBytes()
    {
        HybridCpuInstructionWord[] words = LoopWords(3);
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        IrProgram program = BuildLoopProgram(loopInstructionCount: 3);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        Assert.NotNull(new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model).Witness);
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        Assert.Equal(before, after);
    }

    private static (IrCanonicalLoopV1 Loop, IrLoopMiiReportV1 Mii) Analyze(
        IrProgram program,
        HybridCpuMiiResourceModelV1 model)
    {
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        Assert.Equal(IrCanonicalLoopStatusV1.Qualified, loop.Status);
        IrLoopMiiReportV1 mii = analyzer.ComputeMii(program, loop, model);
        Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, mii.Eligibility);
        return (loop, mii);
    }

    private static IrModuloScheduleStatusV1 InvokeTemporalStatus(IrCanonicalLoopV1 loop, int ii)
    {
        Type scheduler = typeof(HybridCpuModuloSchedulerV1);
        Type countersType = scheduler.GetNestedType("WorkCounters", BindingFlags.NonPublic)!;
        object counters = Activator.CreateInstance(countersType, HybridCpuModuloSchedulerOptionsV1.Production.Budgets)!;
        MethodInfo method = scheduler.GetMethod("SolveTemporal", BindingFlags.NonPublic | BindingFlags.Static)!;
        object solution = method.Invoke(null, [loop, ii, counters])!;
        return Assert.IsType<IrModuloScheduleStatusV1>(solution.GetType().GetProperty("Status")!.GetValue(solution));
    }

    private static HybridCpuMiiResourceModelV1 KnownModel() => HybridCpuMiiResourceModelV1.Create(
        new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16),
        structuralCertificateCapacity: 1);

    private static IrProgram BuildLoopProgram(int loopInstructionCount = 2, bool noOperands = false)
    {
        HybridCpuInstructionWord[] words = noOperands ? NoOperandLoopWords(loopInstructionCount) : LoopWords(loopInstructionCount);
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, words);
        IrInstruction[] instructions = original.Instructions.ToArray();
        IrBasicBlock preheader = new IrBasicBlock(
            0, 0, 0, instructions[0].EncodedAddress, instructions[0].EncodedAddress,
            false, [instructions[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock loop = new IrBasicBlock(
            1, 1, instructions.Length - 1, instructions[1].EncodedAddress, instructions[^1].EncodedAddress,
            false, instructions[1..], [0, 1], [1], false, false, null, [], null, null);
        IrValueFlowGraphV1 valueFlow = original.ValueFlow;
        if (!noOperands)
        {
            IrValueAccessV1 phi = new("native:vt0:x2", 1, IrValueAccessKind.PhiEdgeUse, 1, 1);
            valueFlow = original.ValueFlow with { Accesses = [.. original.ValueFlow.Accesses, phi] };
        }
        return original with
        {
            ControlFlowGraph = new(
                [preheader, loop],
                [new(0, 1, IrControlFlowEdgeKind.Fallthrough), new(1, 1, IrControlFlowEdgeKind.Branch)]),
            ValueFlow = valueFlow
        };
    }

    private static HybridCpuInstructionWord[] NoOperandLoopWords(int loopInstructionCount) =>
        Enumerable.Range(0, loopInstructionCount + 1).Select(_ => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.Nope,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg,
                HybridCpuInstructionWord.NoArchReg)
        }).ToArray();

    private static HybridCpuInstructionWord[] LoopWords(int loopInstructionCount) =>
        Enumerable.Range(0, loopInstructionCount + 1).Select(index => new HybridCpuInstructionWord
        {
            OpCode = (uint)HybridCpuOpcode.ADDI,
            DataTypeValue = HybridCpuDataType.INT64,
            PredicateMask = byte.MaxValue,
            VirtualThreadId = 0,
            Word1 = HybridCpuInstructionWord.PackArchRegs(
                checked((byte)(index % 15 + 1)),
                checked((byte)((index + 15) % 15 + 1)),
                HybridCpuInstructionWord.NoArchReg),
            Immediate = 1
        }).ToArray();

    private static IrProgram TransformLoopInstructions(
        IrProgram program,
        Func<IrInstruction, IrInstruction> transform) =>
        TransformLoopInstructions(program, (instruction, _) => transform(instruction));

    private static IrProgram TransformLoopInstructions(
        IrProgram program,
        Func<IrInstruction, int, IrInstruction> transform)
    {
        int ordinal = 0;
        Dictionary<int, IrInstruction> replacements = program.BasicBlocks.Single(static block => block.Id == 1).Instructions
            .ToDictionary(static instruction => instruction.Index, instruction => transform(instruction, ordinal++));
        IrInstruction[] instructions = program.Instructions
            .Select(instruction => replacements.GetValueOrDefault(instruction.Index, instruction)).ToArray();
        IrBasicBlock[] blocks = program.BasicBlocks.Select(block => block with
        {
            Instructions = block.Instructions.Select(instruction =>
                replacements.GetValueOrDefault(instruction.Index, instruction)).ToArray()
        }).ToArray();
        return program with
        {
            Instructions = instructions,
            ControlFlowGraph = program.ControlFlowGraph with { Blocks = blocks }
        };
    }
}
