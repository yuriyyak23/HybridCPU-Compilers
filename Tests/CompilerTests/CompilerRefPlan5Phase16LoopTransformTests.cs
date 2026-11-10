using System.Globalization;
using System.Reflection;
using HybridCPU.Compiler.Core;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Oracle;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRefPlan5Phase16LoopTransformTests
{
    [Fact]
    public void Contract_IsDefaultOffBoundedAndExactFallbackOnly()
    {
        HybridCpuLoopTransformContractV1 contract = HybridCpuLoopTransformContractV1.Default;
        Assert.Equal("hybridcpu.loop-transform/v1", HybridCpuLoopTransformContractV1.SchemaId);
        Assert.Equal("hybridcpu.modulo-expansion/v1", HybridCpuLoopTransformContractV1.ExpansionSchemaId);
        Assert.Equal("c3c82fae65fdffc058cdbb1aed9721a07372e750711ee5a749285ce750b0b470",
            contract.ContractDigest);
        Assert.Equal("01f8f4017c3762c96e5c82d5ff6506a8de01037a3108734921819e38a19b714e",
            contract.ProductionOptionsDigest);
        Assert.Equal("ee43e5f54042016ec32996a0f408b8b345924266b1098bf10faa64ca2e4565f1",
            contract.QualificationOptionsDigest);
        Assert.Equal(IrLoopTransformSwitchV1.Disabled, contract.DefaultDisposition);
        Assert.Equal(IrRegionOutputDispositionV1.ExactBasicBlockFallbackOnly, contract.OutputDisposition);
        Assert.Equal(HybridCpuModuloSchedulingContractV1.Default.ContractDigest,
            contract.ModuloSchedulerContractDigest);
        Assert.Equal(HybridCpuLoopMiiContractV1.Default.ContractDigest, contract.LoopMiiContractDigest);
        Assert.Equal(new HybridCpuLoopTransformBudgetsV1(64, 256, 4096, 4, 300, 16, 200, 1024),
            HybridCpuLoopTransformOptionsV1.Production.Budgets);
        Assert.Equal([1, 2, 4, 8], HybridCpuLoopTransformOptionsV1.Production.CandidateUnrollFactors);
    }

    [Fact]
    public void ProductionKillSwitch_ReturnsExactFallbackWithoutTransformedProgram()
    {
        Subject subject = Analyze(BuildLoopProgram(3));
        HybridCpuLoopTransformV1 transform = new HybridCpuLoopTransformV1();
        IrLoopTransformResultV1 expansion = transform.MaterializeModuloExpansion(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 8, subject.Model);
        IrLoopTransformResultV1 unroll = transform.TransformArchitectureDrivenUnroll(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 8, subject.Model);

        Assert.Equal(IrLoopTransformStatusV1.DisabledFallback, expansion.Status);
        Assert.Equal(IrLoopTransformStatusV1.DisabledFallback, unroll.Status);
        Assert.Equal(subject.Loop.LoopId, expansion.FallbackLoopIdentity);
        Assert.Equal(subject.Loop.LoopId, unroll.FallbackLoopIdentity);
        Assert.Null(expansion.TransformedProgram);
        Assert.Null(unroll.TransformedProgram);
    }

    [Fact]
    public void TransformPipeline_StaysInsideCoreAndHasNoProductionCaller()
    {
        Assembly core = typeof(HybridCpuLoopTransformV1).Assembly;
        Assert.Equal("HybridCPU.Compiler.Core", core.GetName().Name);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("LLVM", StringComparison.OrdinalIgnoreCase) ||
            reference.Name.Contains("HybridCPU_ISE", StringComparison.Ordinal));

        string root = FindRepositoryRoot();
        string[] production = Directory.EnumerateFiles(
                Path.Combine(root, "Compilers", "HybridCPU_Compiler"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("HybridCpuLoopTransformV1.cs", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("LoopTransformContractsV1.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.DoesNotContain(production, path => File.ReadAllText(path)
            .Contains("HybridCpuLoopTransformV1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public void ModuloExpansion_PreservesIterationMappingAndSmallTripSemantics(long tripCount)
    {
        Subject subject = Analyze(BuildLoopProgram(3));
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().MaterializeModuloExpansion(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, tripCount,
            subject.Model, HybridCpuLoopTransformOptionsV1.Qualification);

        Assert.Equal(IrLoopTransformStatusV1.Accepted, result.Status);
        IrModuloExpansionV1 expansion = Assert.IsType<IrModuloExpansionV1>(result.Expansion);
        IrModuloExpandedOperationV1[] operations =
            [.. expansion.Prolog, .. expansion.Kernel, .. expansion.Epilog];
        Assert.Equal(checked((int)tripCount * subject.Loop.Instructions.Count), operations.Length);
        Assert.Equal(Enumerable.Range(0, checked((int)tripCount)),
            operations.Select(static operation => operation.SourceIteration).Distinct().Order());
        Assert.All(operations, operation =>
        {
            Assert.Contains(subject.Loop.Instructions,
                instruction => instruction.Index == operation.OriginalInstructionIndex);
            Assert.Equal(operation.AbsoluteCycle % expansion.InitiationInterval, operation.ModuloCycle);
            Assert.Equal(IrSourceOriginKind.Generated, operation.Instruction.OriginChain.Links[^1].Kind);
            Assert.Equal(operation.Instruction.SourceSpan, operation.Instruction.OriginChain.Links[^1].Span);
        });
        Assert.Equal(operations.Length,
            operations.Select(static operation => (operation.AbsoluteCycle, operation.IssueSlot)).Distinct().Count());
        if (tripCount == 0)
            Assert.All(expansion.LiveOutMapping, static mapping =>
                Assert.Equal($"livein:{mapping.Key}", mapping.Value));
        Assert.False(string.IsNullOrWhiteSpace(expansion.PlacementValidationDigest));
        Assert.False(string.IsNullOrWhiteSpace(expansion.TemporalValidationDigest));
    }

    [Fact]
    public void ModuloExpansion_RotatesPhiValuesAndSelectsFinalLiveOut()
    {
        Subject subject = Analyze(BuildLoopProgram(3));
        IrModuloExpansionV1 expansion = Assert.IsType<IrModuloExpansionV1>(
            new HybridCpuLoopTransformV1().MaterializeModuloExpansion(
                subject.Program, subject.Loop, subject.Mii, subject.Witness, 4,
                subject.Model, HybridCpuLoopTransformOptionsV1.Qualification).Expansion);

        IrExpandedValueBindingV1[] phi = expansion.Prolog.Concat(expansion.Kernel).Concat(expansion.Epilog)
            .SelectMany(static operation => operation.ValueBindings)
            .Where(static binding => binding.AccessKind == IrValueAccessKind.PhiEdgeUse)
            .OrderBy(static binding => binding.SourceIteration).ToArray();
        Assert.NotEmpty(phi);
        Assert.Contains(phi, static binding => binding.SourceIteration == -1 &&
            binding.VersionedValueId.StartsWith("livein:", StringComparison.Ordinal));
        Assert.Contains(phi, static binding => binding.SourceIteration == 2 &&
            binding.VersionedValueId.EndsWith("@iteration:2", StringComparison.Ordinal));
        Assert.All(expansion.LiveOutMapping, static mapping =>
            Assert.EndsWith("@iteration:3", mapping.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void ModuloExpansion_RevalidatesMemoryOrderForEverySourceIteration()
    {
        IrProgram program = TransformLoopInstructions(BuildLoopProgram(2, noOperands: true), instruction =>
            instruction.Index switch
            {
                1 => WithMemoryEffect(instruction, write: true),
                2 => WithMemoryEffect(instruction, write: false),
                _ => instruction
            });
        Subject subject = Analyze(program);
        IrModuloExpansionV1 expansion = Assert.IsType<IrModuloExpansionV1>(
            new HybridCpuLoopTransformV1().MaterializeModuloExpansion(
                subject.Program, subject.Loop, subject.Mii, subject.Witness, 5,
                subject.Model, HybridCpuLoopTransformOptionsV1.Qualification).Expansion);
        IrModuloExpandedOperationV1[] operations =
            [.. expansion.Prolog, .. expansion.Kernel, .. expansion.Epilog];

        foreach (int iteration in Enumerable.Range(0, 5))
        {
            IrModuloExpandedOperationV1 write = Assert.Single(operations,
                operation => operation.SourceIteration == iteration && operation.OriginalInstructionIndex == 1);
            IrModuloExpandedOperationV1 read = Assert.Single(operations,
                operation => operation.SourceIteration == iteration && operation.OriginalInstructionIndex == 2);
            Assert.True(read.AbsoluteCycle >= write.AbsoluteCycle);
        }
        Assert.False(string.IsNullOrWhiteSpace(expansion.TemporalValidationDigest));
    }

    [Fact]
    public void StaleOrTamperedKernelWitness_IsRejectedBeforeExpansion()
    {
        Subject subject = Analyze(BuildLoopProgram(3));
        IrModuloScheduleWitnessV1 tampered = subject.Witness with
        {
            Operations =
            [
                subject.Witness.Operations[0] with { IssueSlot = 99 },
                .. subject.Witness.Operations.Skip(1)
            ]
        };
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().MaterializeModuloExpansion(
            subject.Program, subject.Loop, subject.Mii, tampered, 4,
            subject.Model, HybridCpuLoopTransformOptionsV1.Qualification);
        Assert.Equal(IrLoopTransformStatusV1.StaleProof, result.Status);
        Assert.Null(result.Expansion);
        Assert.Equal(subject.Loop.LoopId, result.FallbackLoopIdentity);
    }

    [Fact]
    public void MultipleExitAndMaterializationBudgetControlsFailClosed()
    {
        Subject subject = Analyze(BuildLoopProgram(3));
        HybridCpuLoopTransformV1 transform = new HybridCpuLoopTransformV1();
        IrLoopTransformResultV1 exits = transform.MaterializeModuloExpansion(
            subject.Program, subject.Loop with { ExitBlockIds = [7, 8] }, subject.Mii,
            subject.Witness, 4, subject.Model, HybridCpuLoopTransformOptionsV1.Qualification);
        Assert.NotEqual(IrLoopTransformStatusV1.Accepted, exits.Status);
        Assert.Null(exits.Expansion);

        HybridCpuLoopTransformOptionsV1 tiny = HybridCpuLoopTransformOptionsV1.Create(
            HybridCpuLoopTransformBudgetsV1.Production with { MaximumMaterializedOperations = 2 },
            [1, 2, 4, 8],
            IrLoopTransformSwitchV1.ExplicitQualificationOnly,
            IrLoopTransformSwitchV1.ExplicitQualificationOnly,
            IrLoopTransformSwitchV1.ExplicitQualificationOnly,
            IrLoopTransformSwitchV1.ExplicitQualificationOnly);
        Assert.Equal(IrLoopTransformStatusV1.BudgetExhausted,
            transform.MaterializeModuloExpansion(
                subject.Program, subject.Loop, subject.Mii, subject.Witness, 4,
                subject.Model, tiny).Status);
    }

    [Theory]
    [InlineData(IrSlotClass.DmaStreamClass)]
    [InlineData(IrSlotClass.SystemSingleton)]
    public void Lane6AndLane7Pressure_IsRecomputedAndCannotCreateFalseKpiWin(IrSlotClass slotClass)
    {
        IrProgram program = TransformLoopInstructions(
            BuildLoopProgram(2, noOperands: true), instruction => instruction with
            {
                Annotation = instruction.Annotation with { RequiredSlotClass = slotClass }
            });
        Subject subject = Analyze(program);
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().TransformArchitectureDrivenUnroll(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 32,
            subject.Model, options: HybridCpuLoopTransformOptionsV1.Qualification);
        Assert.Equal(IrLoopTransformStatusV1.DisabledFallback, result.Status);
        Assert.Contains(result.Candidates, candidate => candidate.Factor > 1 &&
            candidate.Status is IrLoopTransformCandidateStatusV1.NoKpiBenefit or
                IrLoopTransformCandidateStatusV1.ScheduleRejected);
    }

    [Fact]
    public void ArchitectureUnroll_AcceptsW8BenefitAndRecomputesEveryInvalidatedFact()
    {
        Subject subject = Analyze(BuildLoopProgram(3, noOperands: true));
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().TransformArchitectureDrivenUnroll(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 32,
            subject.Model, options: HybridCpuLoopTransformOptionsV1.Qualification);

        Assert.Equal(IrLoopTransformStatusV1.Accepted, result.Status);
        IrProgram transformed = Assert.IsType<IrProgram>(result.TransformedProgram);
        IrCanonicalLoopV1 loop = Assert.IsType<IrCanonicalLoopV1>(result.TransformedLoop);
        IrLoopMiiReportV1 mii = Assert.IsType<IrLoopMiiReportV1>(result.RecomputedMiiSummary);
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(result.RecomputedWitness);
        IrLoopTransformFreshFactsV1 facts = Assert.IsType<IrLoopTransformFreshFactsV1>(result.FreshFacts);
        Assert.True(result.ProfitabilityEvidence!.BenefitBasisPoints >=
            HybridCpuLoopTransformOptionsV1.Qualification.Budgets.MinimumBenefitBasisPoints);
        Assert.True(result.ProfitabilityEvidence.SourceIterationsPerCandidateIteration > 1);
        Assert.NotEqual(subject.Program.Contract.DerivedFacts.ProgramMutation,
            transformed.Contract.DerivedFacts.ProgramMutation);
        Assert.Equal(transformed.Contract.DerivedFacts.ProgramMutation, loop.MutationStamp);
        Assert.Equal(loop.DistanceDagDigest, facts.DependencyDigest);
        Assert.Equal(loop.ValueAnalysis.ValueFlowDigest, facts.ValueFlowDigest);
        Assert.Equal(mii.ProofStamp.ProofDigest, facts.MiiProofDigest);
        Assert.Equal(witness.WitnessDigest, facts.PlacementWitnessDigest);
        Assert.Equal(AllFacts(), result.InvalidatedAnalysisKinds);
        Assert.Equal(AllFacts(), facts.RecomputedAnalysisKinds);
        Assert.All(transformed.Instructions.Where(instruction => loop.Instructions.Contains(instruction)),
            static instruction => Assert.Equal(IrSourceOriginKind.Generated, instruction.OriginChain.Links[^1].Kind));
        Assert.Equal(IrModuloScheduleStatusV1.Feasible,
            new HybridCpuModuloSchedulerV1().ValidateWitness(
                transformed, loop, mii, witness, subject.Model).Status);
    }

    [Fact]
    public void PreTransformLoopMiiAndWitness_AreStaleForAcceptedCandidate()
    {
        Subject subject = Analyze(BuildLoopProgram(3, noOperands: true));
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().TransformArchitectureDrivenUnroll(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 32,
            subject.Model, options: HybridCpuLoopTransformOptionsV1.Qualification);
        IrProgram transformed = result.TransformedProgram!;

        Assert.Equal(IrLoopMiiEligibilityV1.StaleProof,
            new HybridCpuLoopMiiAnalyzerV1().ComputeMii(
                transformed, subject.Loop, subject.Model).Eligibility);
        Assert.Equal(IrModuloScheduleStatusV1.StaleProof,
            new HybridCpuModuloSchedulerV1().ValidateWitness(
                transformed, subject.Loop, subject.Mii, subject.Witness, subject.Model).Status);
    }

    [Fact]
    public void RemainderAndOracleComparison_AreExplicitForAcceptedCandidate()
    {
        Subject subject = Analyze(BuildLoopProgram(3, noOperands: true));
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().TransformArchitectureDrivenUnroll(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 35,
            subject.Model, options: HybridCpuLoopTransformOptionsV1.Qualification);
        Assert.Equal(IrLoopTransformStatusV1.Accepted, result.Status);
        Assert.Equal(35 % result.ProfitabilityEvidence!.SourceIterationsPerCandidateIteration,
            result.ProfitabilityEvidence.RemainderIterations);
        Assert.Equal(subject.Loop.LoopId, result.FallbackLoopIdentity);

        IrExactOracleMinimumReportV1 oracle = new HybridCpuEnumerativeExactOracleV1().FindMinimum(
            result.TransformedProgram!, result.TransformedLoop!, result.RecomputedMiiSummary!,
            resourceModel: subject.Model);
        Assert.Equal(HybridCpuExactOracleStatusV1.Sat, oracle.Status);
        Assert.NotNull(oracle.MinimumFeasibleIi);
        Assert.Equal(IrModuloScheduleStatusV1.Feasible,
            new HybridCpuModuloSchedulerV1().ValidateWitness(
                result.TransformedProgram!, result.TransformedLoop!, result.RecomputedMiiSummary!,
                result.RecomputedWitness!, subject.Model).Status);
    }

    [Fact]
    public void UnknownTripCodeGrowthAndInvalidProfile_FailClosed()
    {
        Subject subject = Analyze(BuildLoopProgram(3, noOperands: true));
        HybridCpuLoopTransformV1 transform = new HybridCpuLoopTransformV1();
        Assert.Equal(IrLoopTransformStatusV1.DisabledFallback,
            transform.TransformArchitectureDrivenUnroll(
                subject.Program, subject.Loop, subject.Mii, subject.Witness, null,
                subject.Model, options: HybridCpuLoopTransformOptionsV1.Qualification).Status);

        HybridCpuLoopTransformOptionsV1 tight = HybridCpuLoopTransformOptionsV1.Create(
            HybridCpuLoopTransformBudgetsV1.Production with { MaximumCodeGrowthPercent = 0 },
            [1, 2, 4, 8],
            IrLoopTransformSwitchV1.ExplicitQualificationOnly,
            IrLoopTransformSwitchV1.ExplicitQualificationOnly,
            IrLoopTransformSwitchV1.ExplicitQualificationOnly,
            IrLoopTransformSwitchV1.ExplicitQualificationOnly);
        IrLoopTransformResultV1 limited = transform.TransformArchitectureDrivenUnroll(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 32, subject.Model, options: tight);
        Assert.Equal(IrLoopTransformStatusV1.DisabledFallback, limited.Status);
        Assert.All(limited.Candidates.Where(static candidate => candidate.Factor > 1), static candidate =>
            Assert.Equal(IrLoopTransformCandidateStatusV1.CodeGrowthLimit, candidate.Status));

        IrLoopTransformProfileV1 invalidProfile = new IrLoopTransformProfileV1(
            IrLoopTransformProfileDispositionV1.ProfitabilityOnly, 32, "not-a-digest");
        Assert.Equal(IrLoopTransformStatusV1.InvalidModel,
            transform.TransformArchitectureDrivenUnroll(
                subject.Program, subject.Loop, subject.Mii, subject.Witness, 32,
                subject.Model, invalidProfile, HybridCpuLoopTransformOptionsV1.Qualification).Status);
    }

    [Fact]
    public void ReorderedContainersCultureAndProfile_DoNotChangeLegalCandidateOrSchedule()
    {
        IrProgram firstProgram = BuildLoopProgram(3, noOperands: true);
        IrProgram secondProgram = firstProgram with
        {
            ControlFlowGraph = firstProgram.ControlFlowGraph with
            {
                Blocks = firstProgram.BasicBlocks.Reverse().ToArray(),
                Edges = firstProgram.ControlFlowGraph.Edges.Reverse().ToArray()
            }
        };
        Subject first = Analyze(firstProgram);
        Subject second = Analyze(secondProgram);
        HybridCpuLoopTransformV1 transform = new HybridCpuLoopTransformV1();
        CultureInfo original = CultureInfo.CurrentCulture;
        IrLoopTransformResultV1 a;
        IrLoopTransformResultV1 b;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            a = transform.TransformArchitectureDrivenUnroll(
                first.Program, first.Loop, first.Mii, first.Witness, 32, first.Model,
                options: HybridCpuLoopTransformOptionsV1.Qualification);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            b = transform.TransformArchitectureDrivenUnroll(
                second.Program, second.Loop, second.Mii, second.Witness, 32, second.Model,
                new(IrLoopTransformProfileDispositionV1.ProfitabilityOnly, 32, new string('a', 64)),
                HybridCpuLoopTransformOptionsV1.Qualification);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(IrLoopTransformStatusV1.Accepted, a.Status);
        Assert.Equal(a.ProfitabilityEvidence!.SourceIterationsPerCandidateIteration,
            b.ProfitabilityEvidence!.SourceIterationsPerCandidateIteration);
        Assert.Equal(a.RecomputedWitness!.WitnessDigest, b.RecomputedWitness!.WitnessDigest);
        Assert.Equal(a.TransformedLoop!.VersionStamp, b.TransformedLoop!.VersionStamp);
    }

    [Fact]
    public void TransformAnalysis_DoesNotChangeNativeEmission()
    {
        HybridCpuInstructionWord[] words = LoopWords(3);
        byte[] before = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        Subject subject = Analyze(BuildLoopProgram(3));
        _ = new HybridCpuLoopTransformV1().MaterializeModuloExpansion(
            subject.Program, subject.Loop, subject.Mii, subject.Witness, 8,
            subject.Model, HybridCpuLoopTransformOptionsV1.Qualification);
        byte[] after = HybridCpuCanonicalCompiler.CompileProgram(0, words).ProgramImage;
        Assert.Equal(before, after);
    }

    [Fact]
    public void IndependentAdjacentLoops_FuseWithFreshFactsAndKpiBenefit()
    {
        PairSubject subject = AnalyzePair(BuildAdjacentLoopProgram());
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().TransformLoopFusion(
            subject.Program,
            subject.First.Loop,
            subject.First.Mii,
            subject.First.Witness,
            IrLoopIterationDomainProofV1.Create(subject.First.Loop, 0, 32, 1),
            subject.Second.Loop,
            subject.Second.Mii,
            subject.Second.Witness,
            IrLoopIterationDomainProofV1.Create(subject.Second.Loop, 0, 32, 1),
            subject.Model,
            HybridCpuLoopTransformOptionsV1.Qualification);

        Assert.Equal(IrLoopTransformStatusV1.Accepted, result.Status);
        Assert.Equal(IrLoopTransformKindV1.LoopFusion, result.Kind);
        Assert.True(result.ProfitabilityEvidence!.BenefitBasisPoints >= 200);
        Assert.Equal(AllFacts(), result.InvalidatedAnalysisKinds);
        Assert.Equal(AllFacts(), result.FreshFacts!.RecomputedAnalysisKinds);
        Assert.DoesNotContain(result.TransformedProgram!.BasicBlocks,
            block => block.Id == subject.Second.Loop.HeaderBlockId ||
                block.Id == subject.Second.Loop.PreheaderBlockId);
        Assert.Equal(subject.First.Loop.Instructions.Count + subject.Second.Loop.Instructions.Count,
            result.TransformedLoop!.Instructions.Count);
        Assert.Equal(IrModuloScheduleStatusV1.Feasible,
            new HybridCpuModuloSchedulerV1().ValidateWitness(
                result.TransformedProgram, result.TransformedLoop, result.RecomputedMiiSummary!,
                result.RecomputedWitness!, subject.Model).Status);
    }

    [Fact]
    public void Fusion_MismatchedDomainMayAliasAndStaleWitnessFailClosed()
    {
        PairSubject subject = AnalyzePair(BuildAdjacentLoopProgram());
        HybridCpuLoopTransformV1 transform = new HybridCpuLoopTransformV1();
        IrLoopIterationDomainProofV1 first = IrLoopIterationDomainProofV1.Create(
            subject.First.Loop, 0, 32, 1);
        IrLoopIterationDomainProofV1 second = IrLoopIterationDomainProofV1.Create(
            subject.Second.Loop, 0, 31, 1);
        Assert.Equal(IrLoopTransformStatusV1.Ineligible,
            transform.TransformLoopFusion(
                subject.Program, subject.First.Loop, subject.First.Mii, subject.First.Witness, first,
                subject.Second.Loop, subject.Second.Mii, subject.Second.Witness, second,
                subject.Model, HybridCpuLoopTransformOptionsV1.Qualification).Status);

        IrModuloScheduleWitnessV1 stale = subject.Second.Witness with
        {
            LoopVersionStamp = new string('0', 64)
        };
        second = IrLoopIterationDomainProofV1.Create(subject.Second.Loop, 0, 32, 1);
        Assert.Equal(IrLoopTransformStatusV1.StaleProof,
            transform.TransformLoopFusion(
                subject.Program, subject.First.Loop, subject.First.Mii, subject.First.Witness, first,
                subject.Second.Loop, subject.Second.Mii, stale, second,
                subject.Model, HybridCpuLoopTransformOptionsV1.Qualification).Status);

        PairSubject alias = AnalyzePair(BuildAdjacentLoopProgram(mayAlias: true));
        Assert.Equal(IrLoopTransformStatusV1.Ineligible,
            transform.TransformLoopFusion(
                alias.Program, alias.First.Loop, alias.First.Mii, alias.First.Witness,
                IrLoopIterationDomainProofV1.Create(alias.First.Loop, 0, 32, 1),
                alias.Second.Loop, alias.Second.Mii, alias.Second.Witness,
                IrLoopIterationDomainProofV1.Create(alias.Second.Loop, 0, 32, 1),
                alias.Model, HybridCpuLoopTransformOptionsV1.Qualification).Status);

        IrProgram namedBoundaryProgram = BuildAdjacentLoopProgram();
        IrInstruction connector = namedBoundaryProgram.BasicBlocks.Single(block => block.Id == 2).Instructions.Single();
        namedBoundaryProgram = namedBoundaryProgram with
        {
            Labels =
            [
                new("externally_visible_connector", connector.Index, connector.EncodedAddress,
                    2, false, false, null, null, connector.SourceSpan)
            ]
        };
        PairSubject namedBoundary = AnalyzePair(namedBoundaryProgram);
        Assert.Equal(IrLoopTransformStatusV1.Ineligible,
            transform.TransformLoopFusion(
                namedBoundary.Program,
                namedBoundary.First.Loop,
                namedBoundary.First.Mii,
                namedBoundary.First.Witness,
                IrLoopIterationDomainProofV1.Create(namedBoundary.First.Loop, 0, 32, 1),
                namedBoundary.Second.Loop,
                namedBoundary.Second.Mii,
                namedBoundary.Second.Witness,
                IrLoopIterationDomainProofV1.Create(namedBoundary.Second.Loop, 0, 32, 1),
                namedBoundary.Model,
                HybridCpuLoopTransformOptionsV1.Qualification).Status);
    }

    [Fact]
    public void PerfectNest_UnrollsOuterAndJamsInnerWithFreshOuterAndInnerProofs()
    {
        PairSubject nest = AnalyzeNest(BuildPerfectNestProgram());
        IrLoopTransformResultV1 result = new HybridCpuLoopTransformV1().TransformUnrollAndJam(
            nest.Program,
            nest.First.Loop,
            nest.First.Mii,
            nest.First.Witness,
            IrLoopIterationDomainProofV1.Create(nest.First.Loop, 0, 32, 1),
            nest.Second.Loop,
            nest.Second.Mii,
            nest.Second.Witness,
            IrLoopIterationDomainProofV1.Create(nest.Second.Loop, 0, 16, 1),
            nest.Model,
            HybridCpuLoopTransformOptionsV1.Qualification);

        Assert.True(result.Status == IrLoopTransformStatusV1.Accepted,
            $"status={result.Status}; diagnostics={string.Join('|', result.Diagnostics.Select(static diagnostic => diagnostic.Message))}; candidates={string.Join('|', result.Candidates.Select(static candidate => $"{candidate.Factor}:{candidate.Status}:{candidate.RecomputedIi}:{candidate.BenefitBasisPoints}:{candidate.Reason}"))}");
        Assert.Equal(IrLoopTransformKindV1.UnrollAndJam, result.Kind);
        int factor = result.ProfitabilityEvidence!.SourceIterationsPerCandidateIteration;
        Assert.Contains(factor, new[] { 2, 4 });
        Assert.True(result.ProfitabilityEvidence.BenefitBasisPoints >= 200);
        Assert.Equal(AllFacts(), result.FreshFacts!.RecomputedAnalysisKinds);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1[] transformedLoops = analyzer.Canonicalize(
            result.TransformedProgram!, nest.Model).Loops.ToArray();
        IrCanonicalLoopV1 outer = Assert.Single(transformedLoops,
            loop => loop.HeaderBlockId == nest.First.Loop.HeaderBlockId);
        IrCanonicalLoopV1 inner = Assert.Single(transformedLoops,
            loop => loop.HeaderBlockId == nest.Second.Loop.HeaderBlockId);
        Assert.Equal(
            nest.First.Loop.Instructions.Count - nest.Second.Loop.Instructions.Count +
            nest.Second.Loop.Instructions.Count * factor,
            outer.Instructions.Count);
        Assert.Equal(nest.Second.Loop.Instructions.Count * factor, inner.Instructions.Count);
        IrLoopMiiReportV1 innerMii = analyzer.ComputeMii(result.TransformedProgram!, inner, nest.Model);
        Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, innerMii.Eligibility);
        Assert.Equal(IrModuloScheduleStatusV1.Feasible,
            new HybridCpuModuloSchedulerV1().Schedule(
                result.TransformedProgram!, inner, innerMii, nest.Model).Status);
    }

    [Fact]
    public void UnrollAndJam_NonPerfectNestAndStaleProofFailClosed()
    {
        PairSubject nest = AnalyzeNest(BuildPerfectNestProgram());
        HybridCpuLoopTransformV1 transform = new HybridCpuLoopTransformV1();
        IrLoopIterationDomainProofV1 outerDomain = IrLoopIterationDomainProofV1.Create(
            nest.First.Loop, 0, 32, 1);
        IrLoopIterationDomainProofV1 innerDomain = IrLoopIterationDomainProofV1.Create(
            nest.Second.Loop, 0, 16, 1);
        Assert.Equal(IrLoopTransformStatusV1.Ineligible,
            transform.TransformUnrollAndJam(
                nest.Program, nest.First.Loop, nest.First.Mii, nest.First.Witness, outerDomain,
                nest.Second.Loop with { PreheaderBlockId = 0 }, nest.Second.Mii, nest.Second.Witness,
                innerDomain, nest.Model, HybridCpuLoopTransformOptionsV1.Qualification).Status);

        IrModuloScheduleWitnessV1 stale = nest.First.Witness with
        {
            LoopVersionStamp = new string('0', 64)
        };
        Assert.Equal(IrLoopTransformStatusV1.StaleProof,
            transform.TransformUnrollAndJam(
                nest.Program, nest.First.Loop, nest.First.Mii, stale, outerDomain,
                nest.Second.Loop, nest.Second.Mii, nest.Second.Witness, innerDomain,
                nest.Model, HybridCpuLoopTransformOptionsV1.Qualification).Status);
    }

    private static IrLoopInvalidatedAnalysisKindV1 AllFacts() =>
        IrLoopInvalidatedAnalysisKindV1.CanonicalCapabilityAndSideEffects |
        IrLoopInvalidatedAnalysisKindV1.DependencyDag |
        IrLoopInvalidatedAnalysisKindV1.LoopDistances |
        IrLoopInvalidatedAnalysisKindV1.Liveness |
        IrLoopInvalidatedAnalysisKindV1.Pressure |
        IrLoopInvalidatedAnalysisKindV1.ResourceModel |
        IrLoopInvalidatedAnalysisKindV1.Mii |
        IrLoopInvalidatedAnalysisKindV1.Placement;

    private static Subject Analyze(IrProgram program)
    {
        HybridCpuMiiResourceModelV1 model = HybridCpuMiiResourceModelV1.Create(
            new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 1);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1 loop = Assert.Single(analyzer.Canonicalize(program, model).Loops);
        Assert.Equal(IrCanonicalLoopStatusV1.Qualified, loop.Status);
        IrLoopMiiReportV1 mii = analyzer.ComputeMii(program, loop, model);
        Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, mii.Eligibility);
        IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(
            new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model).Witness);
        return new(program, loop, mii, witness, model);
    }

    private static PairSubject AnalyzePair(IrProgram program)
    {
        HybridCpuMiiResourceModelV1 model = HybridCpuMiiResourceModelV1.Create(
            new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 1);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1[] loops = analyzer.Canonicalize(program, model).Loops
            .OrderBy(static loop => loop.HeaderBlockId).ToArray();
        Assert.Equal(2, loops.Length);
        Subject Build(IrCanonicalLoopV1 loop)
        {
            Assert.Equal(IrCanonicalLoopStatusV1.Qualified, loop.Status);
            IrLoopMiiReportV1 mii = analyzer.ComputeMii(program, loop, model);
            Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, mii.Eligibility);
            IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(
                new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model).Witness);
            return new(program, loop, mii, witness, model);
        }
        return new(program, Build(loops[0]), Build(loops[1]), model);
    }

    private static PairSubject AnalyzeNest(IrProgram program)
    {
        HybridCpuMiiResourceModelV1 model = HybridCpuMiiResourceModelV1.Create(
            new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 1);
        HybridCpuLoopMiiAnalyzerV1 analyzer = new HybridCpuLoopMiiAnalyzerV1();
        IrCanonicalLoopV1[] loops = analyzer.Canonicalize(program, model).Loops.ToArray();
        Assert.Equal(2, loops.Length);
        IrCanonicalLoopV1 outer = Assert.Single(loops, static loop => loop.BlockIds.Count == 4);
        IrCanonicalLoopV1 inner = Assert.Single(loops, static loop => loop.BlockIds.Count == 2);
        Subject Build(IrCanonicalLoopV1 loop)
        {
            Assert.Equal(IrCanonicalLoopStatusV1.Qualified, loop.Status);
            IrLoopMiiReportV1 mii = analyzer.ComputeMii(program, loop, model);
            Assert.Equal(IrLoopMiiEligibilityV1.EligibleLowerBound, mii.Eligibility);
            IrModuloScheduleWitnessV1 witness = Assert.IsType<IrModuloScheduleWitnessV1>(
                new HybridCpuModuloSchedulerV1().Schedule(program, loop, mii, model).Witness);
            return new(program, loop, mii, witness, model);
        }
        return new(program, Build(outer), Build(inner), model);
    }

    private static IrProgram BuildLoopProgram(int loopInstructionCount, bool noOperands = false)
    {
        HybridCpuInstructionWord[] words = noOperands
            ? NoOperandLoopWords(loopInstructionCount)
            : LoopWords(loopInstructionCount);
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, words);
        IrInstruction[] instructions = original.Instructions.ToArray();
        IrBasicBlock preheader = new IrBasicBlock(
            0, 0, 0, instructions[0].EncodedAddress, instructions[0].EncodedAddress,
            false, [instructions[0]], [], [1], false, false, null, [], null, null);
        IrBasicBlock loop = new IrBasicBlock(
            1, 1, instructions.Length - 1, instructions[1].EncodedAddress, instructions[^1].EncodedAddress,
            false, instructions[1..], [0, 1], [1], false, false, null, [], null, null);
        IrValueFlowGraphV1 flow = original.ValueFlow;
        if (!noOperands)
            flow = flow with
            {
                Accesses = [.. flow.Accesses, new("native:vt0:x2", 1, IrValueAccessKind.PhiEdgeUse, 1, 1)]
            };
        return original with
        {
            ControlFlowGraph = new(
                [preheader, loop],
                [new(0, 1, IrControlFlowEdgeKind.Fallthrough), new(1, 1, IrControlFlowEdgeKind.Branch)]),
            ValueFlow = flow
        };
    }

    private static IrProgram TransformLoopInstructions(
        IrProgram program,
        Func<IrInstruction, IrInstruction> transform)
    {
        Dictionary<int, IrInstruction> replacements = program.BasicBlocks
            .Single(static block => block.Id == 1).Instructions
            .ToDictionary(static instruction => instruction.Index, transform);
        return program with
        {
            Instructions = program.Instructions.Select(instruction =>
                replacements.GetValueOrDefault(instruction.Index, instruction)).ToArray(),
            ControlFlowGraph = program.ControlFlowGraph with
            {
                Blocks = program.BasicBlocks.Select(block => block with
                {
                    Instructions = block.Instructions.Select(instruction =>
                        replacements.GetValueOrDefault(instruction.Index, instruction)).ToArray()
                }).ToArray()
            }
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

    private static IrProgram BuildAdjacentLoopProgram(bool mayAlias = false)
    {
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, NoOperandLoopWords(6));
        IrInstruction[] instructions = original.Instructions.ToArray();
        if (mayAlias)
        {
            instructions[1] = WithMemoryEffect(instructions[1], write: true);
            instructions[4] = WithMemoryEffect(instructions[4], write: false);
        }
        IrBasicBlock[] blocks =
        [
            Block(0, [instructions[0]], [], [1]),
            Block(1, [instructions[1], instructions[2]], [0, 1], [1, 2]),
            Block(2, [instructions[3]], [1], [3]),
            Block(3, [instructions[4], instructions[5]], [2, 3], [3, 4]),
            Block(4, [instructions[6]], [3], [])
        ];
        IrControlFlowEdge[] edges =
        [
            new(0, 1, IrControlFlowEdgeKind.Fallthrough),
            new(1, 1, IrControlFlowEdgeKind.Branch),
            new(1, 2, IrControlFlowEdgeKind.Fallthrough),
            new(2, 3, IrControlFlowEdgeKind.Fallthrough),
            new(3, 3, IrControlFlowEdgeKind.Branch),
            new(3, 4, IrControlFlowEdgeKind.Fallthrough)
        ];
        return original with
        {
            Instructions = instructions,
            ControlFlowGraph = new(blocks, edges),
            ValueFlow = IrValueFlowGraphV1.Empty
        };

        static IrBasicBlock Block(
            int id,
            IReadOnlyList<IrInstruction> body,
            IReadOnlyList<int> predecessors,
            IReadOnlyList<int> successors) => new(
                id,
                body[0].Index,
                body[^1].Index,
                body[0].EncodedAddress,
                body[^1].EncodedAddress,
                false,
                body,
                predecessors,
                successors,
                successors.Count == 0,
                false,
                null,
                [],
                null,
                null);

    }

    private static IrInstruction WithMemoryEffect(IrInstruction instruction, bool write)
    {
        IrMemoryRegion region = new IrMemoryRegion(0x1000, 4, write);
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

    private static IrProgram BuildPerfectNestProgram()
    {
        IrProgram original = new HybridCpuIrBuilder().BuildProgram(0, NoOperandLoopWords(5));
        IrInstruction[] instructions = original.Instructions.ToArray();
        IrBasicBlock[] blocks =
        [
            Block(0, instructions[0], [], [1]),
            Block(1, instructions[1], [0, 4], [2]),
            Block(2, instructions[2], [1, 3], [3]),
            Block(3, instructions[3], [2], [2, 4]),
            Block(4, instructions[4], [3], [1, 5]),
            Block(5, instructions[5], [4], [])
        ];
        IrControlFlowEdge[] edges =
        [
            new(0, 1, IrControlFlowEdgeKind.Fallthrough),
            new(1, 2, IrControlFlowEdgeKind.Fallthrough),
            new(2, 3, IrControlFlowEdgeKind.Fallthrough),
            new(3, 2, IrControlFlowEdgeKind.Branch),
            new(3, 4, IrControlFlowEdgeKind.Fallthrough),
            new(4, 1, IrControlFlowEdgeKind.Branch),
            new(4, 5, IrControlFlowEdgeKind.Fallthrough)
        ];
        return original with
        {
            ControlFlowGraph = new(blocks, edges),
            ValueFlow = IrValueFlowGraphV1.Empty
        };

        static IrBasicBlock Block(
            int id,
            IrInstruction instruction,
            IReadOnlyList<int> predecessors,
            IReadOnlyList<int> successors) => new(
                id,
                instruction.Index,
                instruction.Index,
                instruction.EncodedAddress,
                instruction.EncodedAddress,
                false,
                [instruction],
                predecessors,
                successors,
                successors.Count == 0,
                false,
                null,
                [],
                null,
                null);
    }

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

    private sealed record Subject(
        IrProgram Program,
        IrCanonicalLoopV1 Loop,
        IrLoopMiiReportV1 Mii,
        IrModuloScheduleWitnessV1 Witness,
        HybridCpuMiiResourceModelV1 Model);

    private sealed record PairSubject(
        IrProgram Program,
        Subject First,
        Subject Second,
        HybridCpuMiiResourceModelV1 Model);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName, "Compilers", "HybridCPU_Compiler")))
            directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
