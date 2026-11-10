using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.RefPlan6.Corpus;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Memory;

namespace HybridCPU_ISE.Tests.CompilerTests;

[CollectionDefinition("RefPlan6 ISE Execution", DisableParallelization = true)]
public sealed class RefPlan6IseExecutionCollection;

[Collection("RefPlan6 ISE Execution")]
public sealed class CompilerRefPlan6Phase01CilCfgSsaTests
{
    private static readonly string FixtureAssembly = typeof(ScalarControlFlowV2Fixtures).Assembly.Location;

    [Theory]
    [InlineData(nameof(ControlFlowCorpus.NestedIfElse), false)]
    [InlineData(nameof(ControlFlowCorpus.ForAccumulator), true)]
    [InlineData(nameof(ControlFlowCorpus.WhileAccumulator), true)]
    [InlineData(nameof(ControlFlowCorpus.DoWhileAccumulator), true)]
    [InlineData(nameof(ControlFlowCorpus.NestedLoops), true)]
    [InlineData(nameof(ControlFlowCorpus.ConditionalExit), true)]
    public void FrozenSdkCorpus_CurrentBuildConfigurationImportsRequiredShape(string method, bool hasLoop)
    {
        string corpusAssembly = Environment.GetEnvironmentVariable("HYBRIDCPU_R6_CORPUS_ASSEMBLY") ??
            typeof(ControlFlowCorpus).Assembly.Location;
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1(
            mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportFile(corpusAssembly,
            new(typeof(ControlFlowCorpus).FullName!, method));

        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            $"{result.Status}: {string.Join(" | ", result.Diagnostics.Select(static item => $"{item.Code}:{item.Message}"))}");
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(result.ControlFlowAnalysis);
        Assert.Equal(hasLoop, analysis.Loops.Count != 0);
        analysis.EnsureWellFormed();
    }

    [Theory]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.ForAccumulator))]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.WhileAccumulator))]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.DoWhileAccumulator))]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.NestedLoops))]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.ConditionalExit))]
    public void RealRoslynLoops_ReachVerifiedCanonicalIr(string method)
    {
        RestrictedCilImportResultV1 result = Import(method);

        Assert.True(result.Status == RestrictedCilImportStatusV1.Success,
            $"{result.Status}: {string.Join(" | ", result.Diagnostics.Select(static item => $"{item.Code}:{item.Message}:{item.StableSourceIdentity}"))}");
        IrProgram program = Assert.IsType<IrProgram>(result.Program);
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(result.ControlFlowAnalysis);
        Assert.NotEmpty(analysis.Loops);
        Assert.All(analysis.Loops, static loop => Assert.True(loop.Reducible));
        Assert.Contains(analysis.Edges, static edge => edge.Kind == ScalarControlFlowV2EdgeKindV1.Backedge);
        Assert.Contains(analysis.Phis, static phi => phi.SlotIdentity.StartsWith("local:", StringComparison.Ordinal));
        Assert.Equal(IrFrontendAdapterStatus.Success, CanonicalIrFrontendBoundaryV1.Validate(program).Status);
        Assert.Contains("frontend.scalar-control-flow-v2/v1", program.Contract.RequiredCapabilities);
        Assert.Contains("optimization.loop-mii-optional-ordinary-fallback/v1", program.Contract.RequiredCapabilities);
    }

    [Theory]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.ForwardDiamond))]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.NestedDiamond))]
    public void RealRoslynForwardMerges_ProduceVerifiedPhis(string method)
    {
        RestrictedCilImportResultV1 result = Import(method);
        Assert.Equal(RestrictedCilImportStatusV1.Success, result.Status);
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(result.ControlFlowAnalysis);
        analysis.EnsureWellFormed();
        Assert.NotEmpty(analysis.Phis);
        Assert.Empty(analysis.Loops);
    }

    [Fact]
    public void LoopCarriedSwap_UsesDeterministicCycleBreakingParallelCopy()
    {
        RestrictedCilImportResultV1 firstImport = Import(nameof(ScalarControlFlowV2Fixtures.SwapLoop));
        RestrictedCilImportResultV1 secondImport = Import(nameof(ScalarControlFlowV2Fixtures.SwapLoop));
        Assert.True(firstImport.Status == RestrictedCilImportStatusV1.Success,
            $"{firstImport.Status}: {string.Join(" | ", firstImport.Diagnostics.Select(static item => $"{item.Code}:{item.Message}"))}");
        ScalarControlFlowV2AnalysisV1 first = Assert.IsType<ScalarControlFlowV2AnalysisV1>(firstImport.ControlFlowAnalysis);
        ScalarControlFlowV2AnalysisV1 second = Assert.IsType<ScalarControlFlowV2AnalysisV1>(secondImport.ControlFlowAnalysis);

        Assert.Contains(first.ParallelCopies, static copy => copy.UsesTemporary);
        Assert.Equal(first.GraphDigest, second.GraphDigest);
        Assert.Equal(first.StateDigest, second.StateDigest);
        Assert.Equal(first.SsaDigest, second.SsaDigest);
        Assert.Equal(first.ParallelCopies, second.ParallelCopies);
        Assert.Equal(64, first.GraphDigest.Length);
        Assert.Equal(64, first.StateDigest.Length);
        Assert.Equal(64, first.SsaDigest.Length);
        Assert.False(first.HasRuntimeAuthority);
        Assert.False(first.HasTargetLegalityAuthority);
    }

    [Fact]
    public void CriticalPhiEdge_IsIdentifiedAndSplitInLoweringPlan()
    {
        RestrictedCilImportResultV1 import = Import(nameof(ScalarControlFlowV2Fixtures.CriticalEdgeLoop));
        Assert.True(import.Status == RestrictedCilImportStatusV1.Success,
            $"{import.Status}: {string.Join(" | ", import.Diagnostics.Select(static item => $"{item.Code}:{item.Message}"))}");
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(import.ControlFlowAnalysis);

        Assert.Contains(analysis.Edges, static edge => edge.IsCritical);
        Assert.Contains(analysis.ParallelCopies, static copy => copy.EdgeWasSplit);
    }

    [Fact]
    public void GraphAndPhiIdentities_AreOrderedAndEdgeComplete()
    {
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(
            Import(nameof(ScalarControlFlowV2Fixtures.ConditionalExit)).ControlFlowAnalysis);
        Assert.Equal(analysis.Blocks.Select(static block => block.Id).Order(),
            analysis.Blocks.Select(static block => block.Id));
        Assert.Equal(analysis.Edges.Select(static edge => edge.StableId).Order(StringComparer.Ordinal),
            analysis.Edges.Select(static edge => edge.StableId));

        foreach (ScalarControlFlowV2BlockV1 block in analysis.Blocks)
        {
            Assert.Equal(block.PredecessorIds.Order(), block.PredecessorIds);
            Assert.Equal(block.SuccessorIds.Order(), block.SuccessorIds);
            Assert.All(block.SuccessorIds, successor => Assert.Contains(block.Id,
                analysis.Blocks.Single(candidate => candidate.Id == successor).PredecessorIds));
        }
        foreach (ScalarControlFlowV2PhiV1 phi in analysis.Phis)
            Assert.Equal(analysis.Blocks.Single(block => block.Id == phi.BlockId).PredecessorIds,
                phi.Incoming.Select(static incoming => incoming.SourceBlockId));
    }

    [Fact]
    public void FrontendAnalysis_FailsFastAfterProgramMutation()
    {
        RestrictedCilImportResultV1 import = Import(nameof(ScalarControlFlowV2Fixtures.ForAccumulator));
        IrProgram program = Assert.IsType<IrProgram>(import.Program);
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(import.ControlFlowAnalysis);
        Assert.True(analysis.IsCurrentFor(program));
        analysis.EnsureCurrentFor(program);

        IrProgram mutated = program with
        {
            Contract = program.Contract with
            {
                DerivedFacts = program.Contract.DerivedFacts.InvalidateAfterScheduleChangingMutation()
            }
        };
        Assert.False(analysis.IsCurrentFor(mutated));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => analysis.EnsureCurrentFor(mutated));
        Assert.Contains("is stale", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SsaVerifier_RejectsMissingPhiIncomingValue()
    {
        ScalarControlFlowV2AnalysisV1 analysis = Assert.IsType<ScalarControlFlowV2AnalysisV1>(
            Import(nameof(ScalarControlFlowV2Fixtures.ForAccumulator)).ControlFlowAnalysis);
        ScalarControlFlowV2PhiV1 phi = analysis.Phis.First(candidate => candidate.Incoming.Count > 1);
        ScalarControlFlowV2AnalysisV1 corrupted = analysis with
        {
            Phis = analysis.Phis.Select(candidate => candidate == phi
                ? candidate with { Incoming = candidate.Incoming.Skip(1).ToArray() }
                : candidate).ToArray()
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(corrupted.EnsureWellFormed);
        Assert.Contains("exactly one ordered incoming", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicBlockBudget_IsDeterministicAndFailsClosed()
    {
        RestrictedCilImportBudgetsV1 production = RestrictedCilImportBudgetsV1.Production;
        RestrictedCilImportBudgetsV1 budgets = production with { MaximumBasicBlocks = 1 };
        RestrictedCilImporterV1 importer = new RestrictedCilImporterV1(budgets: budgets, mode: RestrictedCilImportModeV1.ScalarControlFlowV2);

        RestrictedCilImportResultV1 first = importer.ImportFile(FixtureAssembly,
            new(typeof(ScalarControlFlowV2Fixtures).FullName!, nameof(ScalarControlFlowV2Fixtures.ForAccumulator)));
        RestrictedCilImportResultV1 second = importer.ImportFile(FixtureAssembly,
            new(typeof(ScalarControlFlowV2Fixtures).FullName!, nameof(ScalarControlFlowV2Fixtures.ForAccumulator)));

        Assert.Equal(RestrictedCilImportStatusV1.BudgetExhausted, first.Status);
        Assert.Equal("HCCIL2101", Assert.Single(first.Diagnostics).Code);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Null(first.Program);
        Assert.Null(first.ControlFlowAnalysis);
    }

    [Fact]
    public void CheckedDivision_UsesCurrentScalarControlFlowV2Admission()
    {
        RestrictedCilImportResultV1 result = Import(nameof(ScalarControlFlowV2Fixtures.UnsupportedDivision));

        Assert.Equal(RestrictedCilImportStatusV1.Success, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.IsType<IrProgram>(result.Program);
        Assert.IsType<ScalarControlFlowV2AnalysisV1>(result.ControlFlowAnalysis).EnsureWellFormed();
    }

    [Fact]
    public void BranchIntoMiddleOfInstruction_IsRejectedAsMalformedCfg()
    {
        RestrictedCilImportResultV1 result = ImportMutated(static body => body[6] = 0x0b,
            "branch-mid-instruction.dll");

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, result.Status);
        Assert.Equal("HCCIL0028", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.ControlFlowAnalysis);
    }

    [Fact]
    public void EvaluationStackHeightMismatchAtJoin_IsRejected()
    {
        RestrictedCilImportResultV1 result = ImportMutated(static body =>
        {
            body.Clear();
            byte[] graph = [0x02, 0x2d, 0x02, 0x2b, 0x03, 0x16, 0x2b, 0x02, 0x2b, 0x00, 0x16, 0x2a];
            graph.CopyTo(body[17..]);
        }, "stack-height-join.dll");

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, result.Status);
        Assert.Equal("HCCIL1103", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.ControlFlowAnalysis);
    }

    [Fact]
    public void MultiEntryLoopScc_IsRejectedAsIrreducible()
    {
        RestrictedCilImportResultV1 result = ImportMutated(static body =>
        {
            body.Clear();
            byte[] graph =
            [
                0x02, 0x2d, 0x02, 0x2b, 0x02,
                0x2b, 0x05,
                0x02, 0x2d, 0x02, 0x2b, 0x05,
                0x02, 0x2d, 0xf6, 0x2b, 0xf6,
                0x16, 0x2a
            ];
            graph.CopyTo(body[10..]);
        }, "irreducible-scc.dll");

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Equal("HCCIL1201", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.ControlFlowAnalysis);
    }

    [Fact]
    public void IncompatibleScalarTypesAtStackJoin_AreRejected()
    {
        RestrictedCilImportResultV1 result = ImportMutated(nameof(ScalarControlFlowV2Fixtures.TypedJoinCarrier), static body =>
        {
            body.Clear();
            byte[] graph = [0x02, 0x2d, 0x03, 0x03, 0x2b, 0x03, 0x04, 0x2b, 0x00, 0x2a];
            graph.CopyTo(body[^graph.Length..]);
        }, "incompatible-stack-type-join.dll");

        Assert.Equal(RestrictedCilImportStatusV1.InvalidInput, result.Status);
        Assert.Equal("HCCIL1104", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.ControlFlowAnalysis);
    }

    [Fact]
    public void LegacyMode_StillRejectsBackwardBranchWithStableDiagnostic()
    {
        RestrictedCilImportResultV1 result = new RestrictedCilImporterV1().ImportFile(FixtureAssembly,
            new(typeof(ScalarControlFlowV2Fixtures).FullName!, nameof(ScalarControlFlowV2Fixtures.ForAccumulator)));

        Assert.Equal(RestrictedCilImportStatusV1.Unsupported, result.Status);
        Assert.Equal("HCCIL1021", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.ControlFlowAnalysis);
    }

    [Fact]
    public void ImportedLoop_SchedulesAndBundlesWithOrdinaryScheduler()
    {
        IrProgram program = Assert.IsType<IrProgram>(Import(nameof(ScalarControlFlowV2Fixtures.ForAccumulator)).Program);

        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles,
            resourceModel: HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.True(allocation.Status == IrRegisterAllocationStatusV1.Allocated, allocation.Reason);
        Assert.All(allocation.FinalSchedule.Program.Instructions.SelectMany(static instruction =>
            instruction.Annotation.Defs.Concat(instruction.Annotation.Uses)),
            static operand => Assert.NotEqual(IrOperandKind.VirtualValue, operand.Kind));
        IReadOnlyList<HybridCpuInstructionBundle> lowered = HybridCpuControlFlowRelocationResolver.ApplyRelocations(
            allocation.FinalBundles, new HybridCpuBundleLowerer().LowerProgram(allocation.FinalBundles));
        byte[] image = new HybridCpuBundleSerializer().SerializeProgram(lowered);

        Assert.NotEmpty(image);
    }

    [Fact]
    public void NonModuloLoop_AllocatesWithFreshOrdinaryFactsAndNoManufacturedMii()
    {
        IrProgram program = Assert.IsType<IrProgram>(Import(nameof(ScalarControlFlowV2Fixtures.ConditionalExit)).Program);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles,
            resourceModel: HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);

        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(allocation.Witness);
        Assert.Contains(witness.Rebuild.Loops, static loop => loop.CanonicalStatus == IrCanonicalLoopStatusV1.Unsupported);
        Assert.False(witness.Rebuild.MiiRecomputed);
        Assert.Null(allocation.FinalSchedule.Program.Contract.DerivedFacts.Mii);
        Assert.True(witness.Rebuild.DependenciesCurrent);
        Assert.True(witness.Rebuild.LivenessCurrent);
        Assert.True(witness.Rebuild.PressureCurrent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 10)]
    [InlineData(8, 28)]
    public void ForLoop_ExecutesThroughExistingIseDecodeExecuteRetirePath(int limit, int expected)
        => AssertExecutes(nameof(ScalarControlFlowV2Fixtures.ForAccumulator), limit, expected);

    [Theory]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.WhileAccumulator), 0, 0)]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.WhileAccumulator), 1, 0)]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.WhileAccumulator), 5, 10)]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.DoWhileAccumulator), 0, 0)]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.DoWhileAccumulator), 1, 0)]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.DoWhileAccumulator), 5, 10)]
    [InlineData(nameof(ScalarControlFlowV2Fixtures.ConditionalExit), 5, 2)]
    public void OtherQualifiedLoopShapes_ExecuteThroughExistingIsePath(string method, int limit, int expected)
        => AssertExecutes(method, limit, expected);

    private static void AssertExecutes(string method, int limit, int expected)
    {
        Processor.MainMemoryArea originalMemory = Processor.MainMemory;
        ProcessorMode originalMode = Processor.CurrentProcessorMode;
        MemorySubsystem? originalMemorySubsystem = Processor.Memory;
        try
        {
            Processor.CurrentProcessorMode = ProcessorMode.Compiler;
            Processor.Memory = null;
            Processor.MainMemory = new Processor.MultiBankMemoryArea(4, 0x10000UL);
            byte[] image = CompileAllocated(method, out string layout);
            const ulong returnSentinel = 0x400000;
            Processor.CPU_Core core = new Processor.CPU_Core(0,
                CpuCorePlatformContext.CreateFixed(Processor.MainMemory, ProcessorMode.Compiler));
            core.InitializePipeline();
            core.PrepareExecutionStart(0);
            for (int register = 0; register < 32; register++)
                core.WriteCommittedArch(0, register, 0);
            core.WriteCommittedPc(0, 0);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.StackPointerRegister, 0x10000UL);
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.ReturnAddressRegister,
                HybridCpuNativeCallControlContractV1.Default.BiasReturnSentinel(returnSentinel));
            core.WriteCommittedArch(0, HybridCpuNativeAbiContractV2.Default.ArgumentRegisters[0], unchecked((ulong)(long)limit));

            int retiredBundles = 0;
            List<ulong> trace = new List<ulong>();
            while (core.ReadCommittedPc(0) != returnSentinel && retiredBundles++ < 256)
            {
                ulong pc = core.ReadCommittedPc(0);
                if (trace.Count < 64) trace.Add(pc);
                int bundleIndex = checked((int)(pc / HybridCpuBundleSerializer.BundleSizeBytes));
                Assert.InRange(bundleIndex, 0, image.Length / HybridCpuBundleSerializer.BundleSizeBytes - 1);
                RetireBundle(core, ReadBundle(image, bundleIndex), pc);
                if (core.ReadCommittedPc(0) == pc)
                    core.WriteCommittedPc(0, checked(((pc / HybridCpuBundleSerializer.BundleSizeBytes) + 1) * HybridCpuBundleSerializer.BundleSizeBytes));
            }

            Assert.True(retiredBundles <= 256, $"ISE execution exceeded the deterministic retired-bundle bound; trace={string.Join(',', trace.Select(static pc => pc.ToString("x")))}; final={core.ReadCommittedPc(0):x}; image={DescribeImage(image)}; layout={layout}.");
            Assert.Equal(returnSentinel, core.ReadCommittedPc(0));
            Assert.Equal(unchecked((ulong)(long)expected),
                core.ReadArch(0, HybridCpuNativeAbiContractV2.Default.ReturnRegisters[0]));
            Assert.Equal(0x10000UL, core.ReadArch(0, HybridCpuNativeAbiContractV2.StackPointerRegister));
        }
        finally
        {
            Processor.MainMemory = originalMemory;
            Processor.CurrentProcessorMode = originalMode;
            Processor.Memory = originalMemorySubsystem;
        }
    }

    private static byte[] CompileAllocated(string method, out string layout)
    {
        IrProgram program = Assert.IsType<IrProgram>(Import(method).Program);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrProgramBundlingResult bundles = new HybridCpuBundleFormer().BundleProgram(schedule);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(
            schedule, bundles,
            resourceModel: HybridCpuMiiResourceModelV1.Create(new HybridCpuMachineTopologyV1(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.True(allocation.Status == IrRegisterAllocationStatusV1.Allocated, allocation.Reason);
        int bundleIndex = 0;
        List<string> placements = new List<string>();
        foreach (IrBasicBlockBundlingResult block in allocation.FinalBundles.BlockResults)
            foreach (IrMaterializedBundle bundle in block.Bundles)
            {
                foreach (IrMaterializedBundleSlot slot in bundle.Slots.Where(static slot => slot.Instruction is not null))
                {
                    IrInstruction instruction = slot.Instruction!;
                    placements.Add($"{bundleIndex}:{slot.SlotIndex}:b{block.BlockId}:i{instruction.Index}:{instruction.StableIdentity}:{instruction.Opcode}:{instruction.Annotation.ControlFlowKind}:t{instruction.Annotation.ResolvedBranchTargetInstructionIndex}");
                }
                bundleIndex++;
            }
        IrRegisterAllocationWitnessV1 witness = Assert.IsType<IrRegisterAllocationWitnessV1>(allocation.Witness);
        layout = $"frame={witness.Frame.FrameSizeBytes}:" +
            $"{string.Join(';', witness.Frame.Slots.Select(slot => $"{slot.Identity}@{slot.OffsetFromAdjustedStackPointerBytes}/{slot.SizeBytes}"))}|" +
            string.Join(',', placements);
        HybridCpuBundleLowerer lowerer = new HybridCpuBundleLowerer();
        IReadOnlyList<HybridCpuInstructionBundle> lowered = lowerer.LowerProgram(allocation.FinalBundles);
        IReadOnlyList<HybridCpuInstructionBundle> relocated = HybridCpuControlFlowRelocationResolver.ApplyRelocations(allocation.FinalBundles, lowered);
        return new HybridCpuBundleSerializer().SerializeProgram(relocated);
    }

    private static void RetireBundle(Processor.CPU_Core core, VLIW_Instruction[] bundle, ulong pc)
    {
        bool hasControlFlow = bundle.Any(static instruction => instruction.OpCode is
            >= (uint)Processor.CPU_Core.InstructionsEnum.JAL and
            <= (uint)Processor.CPU_Core.InstructionsEnum.BGEU);
        core.TestRunDecodeStageWithFetchedBundle(bundle, pc);
        core.TestRunExecuteStageFromCurrentDecodeState();
        core.TestRunMemoryAndWriteBackStagesFromCurrentExecuteState();
        // This bounded test driver owns sequential bundle stepping; only the ISE's
        // control-flow retire publication is allowed to select a non-sequential PC.
        if (!hasControlFlow)
            core.WriteCommittedPc(0, pc);
    }

    private static VLIW_Instruction[] ReadBundle(byte[] image, int bundleIndex)
    {
        VLIW_Bundle bundle = new VLIW_Bundle();
        Assert.True(bundle.TryReadBytes(image, checked(bundleIndex * HybridCpuBundleSerializer.BundleSizeBytes)));
        return Enumerable.Range(0, HybridCpuInstructionBundle.SlotCount).Select(bundle.GetInstruction).ToArray();
    }

    private static string DescribeImage(byte[] image) => string.Join('|',
        Enumerable.Range(0, image.Length / HybridCpuBundleSerializer.BundleSizeBytes).Select(index =>
            $"{index}:" + string.Join(',', ReadBundle(image, index).Where(static instruction => instruction.OpCode != 0)
                .Select(static instruction => $"{instruction.OpCode}/{instruction.Immediate:x4}/{instruction.Reg1ID}/{instruction.Reg2ID}/{instruction.Reg3ID}"))));

    private static RestrictedCilImportResultV1 Import(string method) =>
        new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportFile(FixtureAssembly,
            new(typeof(ScalarControlFlowV2Fixtures).FullName!, method));

    private delegate void SpanMutation(Span<byte> body);

    private static RestrictedCilImportResultV1 ImportMutated(SpanMutation mutation, string sourceIdentity) =>
        ImportMutated(nameof(ScalarControlFlowV2Fixtures.ForAccumulator), mutation, sourceIdentity);

    private static RestrictedCilImportResultV1 ImportMutated(string methodName, SpanMutation mutation, string sourceIdentity)
    {
        byte[] image = File.ReadAllBytes(FixtureAssembly);
        using PEReader pe = new PEReader(new MemoryStream(image, writable: false));
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition).Single(candidate =>
            string.Equals(metadata.GetString(candidate.Name), methodName, StringComparison.Ordinal));
        int rva = method.RelativeVirtualAddress;
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(candidate =>
            rva >= candidate.VirtualAddress && rva < candidate.VirtualAddress + Math.Max(candidate.VirtualSize, candidate.SizeOfRawData));
        int bodyOffset = checked(rva - section.VirtualAddress + section.PointerToRawData);
        byte first = image[bodyOffset];
        int headerSize = (first & 3) == 2 ? 1 : (BitConverter.ToUInt16(image, bodyOffset) >> 12) * 4;
        int codeSize = (first & 3) == 2 ? first >> 2 : BitConverter.ToInt32(image, bodyOffset + 4);
        mutation(image.AsSpan(bodyOffset + headerSize, codeSize));
        return new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportImage(image,
            new(typeof(ScalarControlFlowV2Fixtures).FullName!, methodName), sourceIdentity);
    }
}

public static class ScalarControlFlowV2Fixtures
{
    public static int TypedJoinCarrier(bool condition, int integer, long wide)
    {
        if (condition) return unchecked((int)wide);
        return integer;
    }

    public static int ReferenceParameter(object value) => value is null ? 0 : 1;

    public static int UnsupportedDivision(int left, int right) => left / right;

    public static int ForwardDiamond(int value, int condition)
    {
        int result;
        if (condition == 0) result = value + 1;
        else result = value - 1;
        return result;
    }

    public static int NestedDiamond(int value, int first, int second)
    {
        int result;
        if (first == 0)
        {
            if (second == 0) result = value + 1;
            else result = value + 2;
        }
        else result = value - 1;
        return result;
    }

    public static int ForAccumulator(int limit)
    {
        int sum = 0;
        for (int index = 0; index < limit; index++) sum += index;
        return sum;
    }

    public static int WhileAccumulator(int limit)
    {
        int sum = 0;
        int index = 0;
        while (index < limit) { sum += index; index++; }
        return sum;
    }

    public static int DoWhileAccumulator(int limit)
    {
        int sum = 0;
        int index = 0;
        do { sum += index; index++; } while (index < limit);
        return sum;
    }

    public static int NestedLoops(int outer, int inner)
    {
        int sum = 0;
        for (int left = 0; left < outer; left++)
            for (int right = 0; right < inner; right++) sum += left + right;
        return sum;
    }

    public static int ConditionalExit(int limit)
    {
        int sum = 0;
        for (int index = 0; index < limit; index++)
        {
            if (index == 3) break;
            if (index == 1) continue;
            sum += index;
        }
        return sum;
    }

    public static int SwapLoop(int count)
    {
        int left = 1;
        int right = 2;
        int remaining = count;
        while (remaining > 0)
        {
            int temporary = left;
            left = right;
            right = temporary;
            remaining--;
        }
        return left + right;
    }

    public static int CriticalEdgeLoop(int count, int choose)
    {
        int value = 1;
        int remaining = count;
        while (remaining > 0)
        {
            if (choose != 0) value += remaining;
            remaining--;
        }
        return value;
    }
}
