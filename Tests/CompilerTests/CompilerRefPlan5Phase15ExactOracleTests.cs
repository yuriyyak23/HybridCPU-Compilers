using System.Globalization;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Oracle;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase15ExactOracleTests
{
    [Fact]
    public void Contract_IsBoundedSolverNeutralAndForbiddenFromProductionDependencies()
    {
        HybridCpuExactSchedulingOracleContractV1 contract = HybridCpuExactSchedulingOracleContractV1.Default;
        Assert.Equal("hybridcpu.exact-scheduling-oracle/v1", HybridCpuExactSchedulingOracleContractV1.SchemaId);
        Assert.Equal("enumerative-modulo-stage-difference/v1", HybridCpuExactSchedulingOracleContractV1.BackendIdentity);
        Assert.Equal("5d1ec2a8756234104d4960b1f25f4e8aa054af20e08c2d860fb7b44f48baff38",
            contract.ContractDigest);
        Assert.Equal("1036308992335ce56b7466a1b8d9e0b62b07af0b83635f083a3411f7f9f01bad",
            contract.CiOptionsDigest);
        Assert.Equal(HybridCpuTargetMachineContractV1.Default.ContractDigest, contract.TargetDigest);
        Assert.Equal(HybridCpuModuloSchedulingContractV1.Default.ContractDigest,
            contract.ModuloSchedulerContractDigest);
        Assert.Equal(HybridCpuExactOracleOptionsV1.Ci.OptionsDigest, contract.CiOptionsDigest);
        Assert.Equal(HybridCpuOracleProductionDependencyDispositionV1.Forbidden,
            contract.ProductionDependencyDisposition);
        Assert.Equal(new HybridCpuExactOracleBudgetsV1(12, 8, 2_000_000, 2_000_000),
            HybridCpuExactOracleOptionsV1.Ci.Budgets);

        string[] oracleReferences = typeof(HybridCpuEnumerativeExactOracleV1).Assembly.GetReferencedAssemblies()
            .Select(static assembly => assembly.Name!).ToArray();
        Assert.Contains("HybridCPU.Compiler.Core", oracleReferences);
        Assert.DoesNotContain(oracleReferences, static name =>
            name.Contains("LLVM", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Z3", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("HybridCPU_ISE", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(HybridCpuModuloSchedulerV1).Assembly.GetReferencedAssemblies(), static assembly =>
            assembly.Name!.Contains("Oracle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QualifiedLoop_ProducesSatWitnessRevalidatedByCore()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 3);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        int ii = mii.ProvenLowerBoundIi!.Value;
        HybridCpuEnumerativeExactOracleV1 oracle = new HybridCpuEnumerativeExactOracleV1();
        IrExactOracleQueryV1 query = oracle.QueryAtIi(program, loop, mii, ii, model);

        Assert.Equal(HybridCpuExactOracleStatusV1.Sat, query.Status);
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(query.Witness);
        Assert.True(new HybridCpuModuloSchedulerV1().ValidateWitness(program, loop, mii, witness, model).IsValid);
        Assert.Equal(ii, witness.InitiationInterval);
        Assert.NotEmpty(witness.ResourceReservations);
    }

    [Fact]
    public void MinimumSearch_ProvesEveryLowerCandidateAndReportsComparableGap()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 3);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        IrModuloScheduleResultV1 production = new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model);
        IrExactOracleMinimumReportV1 report = new HybridCpuEnumerativeExactOracleV1().FindMinimum(
            program, loop, mii, production, model);

        Assert.Equal(HybridCpuExactOracleStatusV1.Sat, report.Status);
        Assert.NotNull(report.MinimumFeasibleIi);
        Assert.All(report.Queries.TakeWhile(static query => query.Status != HybridCpuExactOracleStatusV1.Sat),
            static query =>
            {
                Assert.Equal(HybridCpuExactOracleStatusV1.Unsat, query.Status);
                Assert.True(query.UnsatProof!.CompleteForComparableContract);
            });
        Assert.True(report.Gap.Comparable);
        Assert.Equal(production.ChosenIi - report.MinimumFeasibleIi, report.Gap.IiGap);
        Assert.True(report.Gap.RelativeIiGap >= 1m);
        Assert.NotNull(report.Gap.ProductionWorkUnits);
        Assert.True(report.Gap.OracleWorkUnits > 0);
    }

    [Fact]
    public void ReorderedContainersAndCultures_ProduceIdenticalExactResults()
    {
        IrProgram first = BuildLoopProgram(loopInstructionCount: 3);
        IrProgram second = first with
        {
            ControlFlowGraph = first.ControlFlowGraph with
            {
                Blocks = first.BasicBlocks.Reverse().ToArray(),
                Edges = first.ControlFlowGraph.Edges.Reverse().ToArray()
            },
            ValueFlow = first.ValueFlow with
            {
                Values = first.ValueFlow.Values.Reverse().ToArray(),
                Accesses = first.ValueFlow.Accesses.Reverse().ToArray()
            }
        };
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 firstLoop, IrLoopMiiReportV1 firstMii) = Analyze(first, model);
        (IrCanonicalLoopV1 secondLoop, IrLoopMiiReportV1 secondMii) = Analyze(second, model);
        HybridCpuEnumerativeExactOracleV1 oracle = new HybridCpuEnumerativeExactOracleV1();
        CultureInfo original = CultureInfo.CurrentCulture;
        IrExactOracleMinimumReportV1 a;
        IrExactOracleMinimumReportV1 b;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            a = oracle.FindMinimum(first, firstLoop, firstMii, resourceModel: model);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            b = oracle.FindMinimum(second, secondLoop, secondMii, resourceModel: model);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.MinimumFeasibleIi, b.MinimumFeasibleIi);
        Assert.Equal(a.Witness!.WitnessDigest, b.Witness!.WitnessDigest);
        Assert.Equal(a.ReportDigest, b.ReportDigest);
    }

    [Fact]
    public void ExactW8Constraint_IsInsideSatSearchAndCanProveUnsat()
    {
        IrProgram program = TransformLoopInstructions(BuildLoopProgram(3, noOperands: true), instruction => instruction with
        {
#pragma warning disable CS0618
            Annotation = instruction.Annotation with { LegalSlots = IrIssueSlotMask.Slot0 }
#pragma warning restore CS0618
        });
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuEnumerativeExactOracleV1 oracle = new HybridCpuEnumerativeExactOracleV1();

        IrExactOracleQueryV1 infeasible = oracle.QueryAtIi(program, loop, mii, 1, model);
        IrExactOracleQueryV1 feasible = oracle.QueryAtIi(program, loop, mii, 3, model);
        Assert.Equal(HybridCpuExactOracleStatusV1.Unsat, infeasible.Status);
        Assert.Contains("shared-exact-w8-placement", infeasible.UnsatProof!.ConstraintCore);
        Assert.Equal(HybridCpuExactOracleStatusV1.Sat, feasible.Status);
        Assert.All(feasible.Witness!.Operations.GroupBy(static operation => operation.ModuloCycle),
            static group => Assert.Single(group));
    }

    [Fact]
    public void DeterministicWorkLimit_IsResourceLimitAndNeverUnsat()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 4);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuExactOracleOptionsV1 options = HybridCpuExactOracleOptionsV1.Create(new(12, 2, 1, 64));
        HybridCpuEnumerativeExactOracleV1 oracle = new HybridCpuEnumerativeExactOracleV1();
        IrExactOracleQueryV1 first = oracle.QueryAtIi(program, loop, mii, mii.ProvenLowerBoundIi!.Value, model, options);
        IrExactOracleQueryV1 second = oracle.QueryAtIi(program, loop, mii, mii.ProvenLowerBoundIi!.Value, model, options);

        Assert.Equal(HybridCpuExactOracleStatusV1.ResourceLimit, first.Status);
        Assert.Equal(HybridCpuExactOracleLimitKindV1.MaximumAssignmentStates, first.LimitKind);
        Assert.True(first.LimitIsDeterministic);
        Assert.Null(first.UnsatProof);
        Assert.Equal(first.ResultDigest, second.ResultDigest);
    }

    [Theory]
    [InlineData(HybridCpuExactOracleLimitKindV1.WallClockWatchdog)]
    [InlineData(HybridCpuExactOracleLimitKindV1.SolverCrash)]
    [InlineData(HybridCpuExactOracleLimitKindV1.UnsupportedSolverFeature)]
    public void OperationalFailure_IsNeverConvertedToUnsat(HybridCpuExactOracleLimitKindV1 kind)
    {
        IrExactOracleQueryV1 result = HybridCpuEnumerativeExactOracleV1.OperationalResourceLimit(
            2, kind, "injected operational failure");
        Assert.Equal(HybridCpuExactOracleStatusV1.ResourceLimit, result.Status);
        Assert.Equal(kind, result.LimitKind);
        Assert.False(result.LimitIsDeterministic);
        Assert.Null(result.UnsatProof);
    }

    [Fact]
    public void DeliberatelyOmittedTemporalConstraint_ProducesRejectedSatWitnessAndInvalidModel()
    {
        IrProgram program = TransformLoopInstructions(BuildLoopProgram(loopInstructionCount: 2),
            (instruction, ordinal) => instruction with
            {
                Annotation = instruction.Annotation with
                {
                    MemoryReadRegion = ordinal == 1 ? new(0x1000, 4, false) : null,
                    MemoryWriteRegion = ordinal == 0 ? new(0x1000, 4, false) : null
                },
                SideEffects = ordinal == 0
                    ? new(new(IrMemoryEffectKind.Write, IrAddressSpaceIdentity.Generic,
                        IrMemoryOrdering.NotAtomic, null, new(0x1000, 4, false)), IrArchitecturalEffectKind.None)
                    : new(new(IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic,
                        IrMemoryOrdering.NotAtomic, new(0x1000, 4, false), null), IrArchitecturalEffectKind.None)
            });
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuExactOracleOptionsV1 broken = HybridCpuExactOracleOptionsV1.Create(
            HybridCpuExactOracleBudgetsV1.Ci, HybridCpuOracleConstraintFamilyV1.TemporalDistance);
        IrExactOracleQueryV1 result = new HybridCpuEnumerativeExactOracleV1().QueryAtIi(
            program, loop, mii, mii.ProvenLowerBoundIi!.Value, model, broken);

        Assert.Equal(HybridCpuExactOracleStatusV1.InvalidModel, result.Status);
        IrModuloScheduleWitnessV1 rejected = Assert.IsType<IrModuloScheduleWitnessV1>(result.RejectedSatWitness);
        Assert.False(new HybridCpuModuloSchedulerV1().ValidateWitness(program, loop, mii, rejected, model).IsValid);
        Assert.False(result.CoreValidation!.IsValid);
        Assert.Null(result.UnsatProof);
    }

    [Fact]
    public void StaleOrForgedCanonicalProofs_AreInvalidModelNotUnsat()
    {
        IrProgram program = BuildLoopProgram();
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuEnumerativeExactOracleV1 oracle = new HybridCpuEnumerativeExactOracleV1();
        Assert.Equal(HybridCpuExactOracleStatusV1.InvalidModel,
            oracle.QueryAtIi(program, loop with { VersionStamp = new string('0', 64) }, mii, 2, model).Status);
        Assert.Equal(HybridCpuExactOracleStatusV1.InvalidModel,
            oracle.QueryAtIi(program, loop, mii with { ProvenLowerBoundIi = 1 }, 2, model).Status);
        HybridCpuExactOracleOptionsV1 malformedOptions = new HybridCpuExactOracleOptionsV1(null!, HybridCpuOracleConstraintFamilyV1.None, string.Empty);
        Assert.Equal(HybridCpuExactOracleStatusV1.InvalidModel,
            oracle.QueryAtIi(program, loop, mii, 2, model, malformedOptions).Status);
        Assert.Equal(HybridCpuExactOracleStatusV1.InvalidModel,
            oracle.FindMinimum(program, loop, mii, resourceModel: model, options: malformedOptions).Status);
    }

    [Fact]
    public void ProvenLowerBoundUnsatCore_PreservesTypedResourceCauses()
    {
        HybridCpuMiiResourceModelV1 wide = KnownModel();
        AssertLowerBoundCore(BuildLoopProgram(3),
            HybridCpuMiiResourceModelV1.Create(new(64, 1, 4, 8, 2, 16), 1),
            IrMiiComponentKindV1.PrfWritePortMii);
        AssertLowerBoundCore(BuildLoopProgram(9, noOperands: true), wide,
            IrMiiComponentKindV1.SlotMii);

        IrProgram memory = TransformLoopInstructions(BuildLoopProgram(2), instruction => instruction with
        {
            Annotation = instruction.Annotation with { MemoryReadRegion = new(0x1000, 4, false) },
            SideEffects = new(
                new(IrMemoryEffectKind.Read, IrAddressSpaceIdentity.Generic, IrMemoryOrdering.NotAtomic,
                    new(0x1000, 4, false), null),
                IrArchitecturalEffectKind.None)
        });
        AssertLowerBoundCore(memory, wide,
            IrMiiComponentKindV1.MemoryBankMii, IrMiiComponentKindV1.MemoryChannelMii);

        IrProgram lanes = TransformLoopInstructions(BuildLoopProgram(4), (instruction, ordinal) => instruction with
        {
            Annotation = instruction.Annotation with
            {
                RequiredSlotClass = ordinal < 3 ? IrSlotClass.DmaStreamClass : IrSlotClass.SystemSingleton
            }
        });
        AssertLowerBoundCore(lanes, HybridCpuMiiResourceModelV1.Create(wide.Topology, 2),
            IrMiiComponentKindV1.Lane6Mii);

        IrProgram certificate = TransformLoopInstructions(BuildLoopProgram(2), (instruction, ordinal) => instruction with
        {
            Annotation = instruction.Annotation with
            {
                RequiredSlotClass = ordinal == 0 ? IrSlotClass.DmaStreamClass : IrSlotClass.SystemSingleton
            }
        });
        AssertCoreAtIi(certificate, wide, 1, IrMiiComponentKindV1.CertificateMii);

        IrProgram lane7 = TransformLoopInstructions(BuildLoopProgram(2), instruction => instruction with
        {
            Annotation = instruction.Annotation with { RequiredSlotClass = IrSlotClass.SystemSingleton }
        });
        AssertLowerBoundCore(lane7, wide, IrMiiComponentKindV1.Lane7Mii);

        AssertCoreAtIi(BuildLoopProgram(9), wide, 1,
            IrMiiComponentKindV1.RecMii, IrMiiComponentKindV1.SlotMii);

        IrMiiComponentResultV1 exactGroupKernel = new(
            IrMiiComponentKindV1.RegisterGroupMii,
            IrMiiComponentStatusV1.Proven,
            2,
            8,
            4,
            IrLoopProofPrecisionV1.Exact,
            ["group:0:peak:8"],
            "phase07-exact-group-pressure",
            HybridCpuTargetMachineContractV1.Default.ContractDigest,
            wide.ModelDigest);
        MethodInfo coreBuilder = typeof(HybridCpuEnumerativeExactOracleV1).GetMethod(
            "BuildProvenLowerBoundCore", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] groupCore = Assert.IsType<string[]>(coreBuilder.Invoke(null, [new[] { exactGroupKernel }, 1]));
        Assert.Contains(groupCore, static item => item.StartsWith("RegisterGroupMii:", StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvedLowerCandidate_IsUnscoredAndCannotClaimMinimum()
    {
        IrProgram program = BuildLoopProgram(loopInstructionCount: 4);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        HybridCpuExactOracleOptionsV1 limited = HybridCpuExactOracleOptionsV1.Create(new(12, 2, 1, 64));
        IrExactOracleMinimumReportV1 report = new HybridCpuEnumerativeExactOracleV1().FindMinimum(
            program, loop, mii, new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model), model, limited);

        Assert.NotEqual(HybridCpuExactOracleStatusV1.Sat, report.Status);
        Assert.Null(report.MinimumFeasibleIi);
        Assert.False(report.Gap.Comparable);
        Assert.Null(report.Gap.IiGap);
        Assert.Contains(report.Queries, static query => query.Status == HybridCpuExactOracleStatusV1.ResourceLimit);
    }

    [Fact]
    public void OracleQueriesAndGapReporting_DoNotChangeNativeEmission()
    {
        HybridCpuInstructionWord[] words = LoopWords(3);
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        IrProgram program = BuildLoopProgram(3);
        HybridCpuMiiResourceModelV1 model = KnownModel();
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        _ = new HybridCpuEnumerativeExactOracleV1().FindMinimum(program, loop, mii,
            new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model), model);
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        Assert.Equal(before, after);
    }

    private static void AssertLowerBoundCore(
        IrProgram program,
        HybridCpuMiiResourceModelV1 model,
        params IrMiiComponentKindV1[] expected)
    {
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        Assert.True(mii.ProvenLowerBoundIi > 1,
            $"lower={mii.ProvenLowerBoundIi}; components={string.Join('|', mii.Components.Select(static item => $"{item.Component}:{item.Value}:{item.Status}"))}");
        AssertCoreAtIi(program, loop, mii, model, mii.ProvenLowerBoundIi!.Value - 1, expected);
    }

    private static void AssertCoreAtIi(
        IrProgram program,
        HybridCpuMiiResourceModelV1 model,
        int initiationInterval,
        params IrMiiComponentKindV1[] expected)
    {
        (IrCanonicalLoopV1 loop, IrLoopMiiReportV1 mii) = Analyze(program, model);
        AssertCoreAtIi(program, loop, mii, model, initiationInterval, expected);
    }

    private static void AssertCoreAtIi(
        IrProgram program,
        IrCanonicalLoopV1 loop,
        IrLoopMiiReportV1 mii,
        HybridCpuMiiResourceModelV1 model,
        int initiationInterval,
        params IrMiiComponentKindV1[] expected)
    {
        IrExactOracleQueryV1 result = new HybridCpuEnumerativeExactOracleV1().QueryAtIi(
            program, loop, mii, initiationInterval, model);
        Assert.Equal(HybridCpuExactOracleStatusV1.Unsat, result.Status);
        foreach (IrMiiComponentKindV1 component in expected)
            Assert.Contains(result.UnsatProof!.ConstraintCore,
                item => item.StartsWith(component + ":", StringComparison.Ordinal));
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
