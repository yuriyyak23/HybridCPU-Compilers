using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerLiteralResultTests
{
    [Fact]
    public void LiteralSwitchArms_DefineValuesReadByPhiCopies()
    {
        Type type = typeof(LiteralResultFixture);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
                File.ReadAllBytes(type.Assembly.Location), "literal-result-fixture",
                [new(type.FullName!, nameof(LiteralResultFixture.Title))], []));
        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCompiledMethodV1 method = graph.Methods.Single(m => m.Identity.MethodName == nameof(LiteralResultFixture.Title));
        Assert.NotEmpty(method.Import.ControlFlowAnalysis!.ParallelCopies);
        IrProgram program = method.Import.Program!;
        HashSet<string> definitions = program.Instructions.SelectMany(i => i.Annotation.Defs)
            .Where(o => o.Kind == IrOperandKind.VirtualValue).Select(o => o.Name).ToHashSet();
        foreach (IrOperand? use in program.Instructions.SelectMany(i => i.Annotation.Uses)
            .Where(o => o.Kind == IrOperandKind.VirtualValue && o.Name!.EndsWith(":value")))
            Assert.True(definitions.Contains(use.Name), "Undefined CIL literal value: " + use.Name);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule,
            new HybridCpuBundleFormer().BundleProgram(schedule),
            resourceModel: HybridCpuMiiResourceModelV1.Create(new(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.Equal(3, allocation.FinalSchedule.Program.Instructions.Count(i =>
            i.StableIdentity.EndsWith(":ldstr:result-copy")));
    }
}

public static class LiteralResultFixture
{
    public static string Title(int mode) => mode switch
    {
        0 => "Retail title",
        1 => "Shareware title",
        _ => "Fallback title"
    };
}
