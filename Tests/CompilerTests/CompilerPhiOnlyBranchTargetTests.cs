using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerPhiOnlyBranchTargetTests
{
    [Fact]
    public void BranchToCopyOnlyBlock_ExecutesPhiCopiesBeforeJoin()
    {
        Type type = typeof(PhiOnlyBranchFixture);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
                File.ReadAllBytes(type.Assembly.Location), "phi-only-fixture",
                [new(type.FullName!, nameof(PhiOnlyBranchFixture.Loop))], []));
        Assert.Equal(RestrictedCilImportStatusV1.Success, graph.Status);
        ManagedCompiledMethodV1 method = graph.Methods.Single(m => m.Identity.MethodName == nameof(PhiOnlyBranchFixture.Loop));
        ScalarControlFlowV2AnalysisV1 analysis = method.Import.ControlFlowAnalysis!;
        ScalarControlFlowV2BlockV1 block = Assert.Single(analysis.Blocks.Where(b => b.EndOffsetExclusive - b.StartOffset == 1 &&
            analysis.ParallelCopies.Any(c => c.SourceBlockId == b.Id)));
        ScalarControlFlowV2ParallelCopyV1 copy = analysis.ParallelCopies.Where(c => c.SourceBlockId == block.Id)
            .OrderBy(c => c.Sequence).First();
        IrProgram program = method.Import.Program!;
        AssertEntry(program, copy.StableId);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule,
            new HybridCpuBundleFormer().BundleProgram(schedule),
            resourceModel: HybridCpuMiiResourceModelV1.Create(new(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        AssertEntry(allocation.FinalSchedule.Program, copy.StableId);
    }

    private static void AssertEntry(IrProgram program, string copyIdentity)
    {
        int firstCopy = program.Instructions.Single(i => i.StableIdentity == copyIdentity).Index;
        Assert.Contains(program.Instructions, i =>
            i.Annotation.ResolvedBranchTargetInstructionIndex == firstCopy);
    }
}

public static class PhiOnlyBranchFixture
{
    public static int Leaf(int value) => value + 2;
    public static int Loop(int limit)
    {
        int result = 0;
        for (int i = 0; i < limit; i++)
        {
            int c = Leaf(i);
            result += c == 1 ? c : Leaf(c);
        }
        return result;
    }
}
