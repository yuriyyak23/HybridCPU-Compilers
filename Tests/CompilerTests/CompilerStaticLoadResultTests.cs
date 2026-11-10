using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerStaticLoadResultTests
{
    [Theory]
    [InlineData(nameof(StaticLoadResultFixture.Reference))]
    [InlineData(nameof(StaticLoadResultFixture.Integer))]
    public void StaticLoadAcrossBlock_HasDefinedReturnValue(string name)
    {
        Type type = typeof(StaticLoadResultFixture);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
                File.ReadAllBytes(type.Assembly.Location), "static-load-result-fixture",
                [new(type.FullName!, name)], []));
        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCompiledMethodV1 method = graph.Methods.Single(m => m.Identity.MethodName == name);
        IrProgram program = method.Import.Program!;
        Assert.True(method.Import.ControlFlowAnalysis!.Blocks.Count > 1);
        HashSet<string> definitions = program.Instructions.SelectMany(i => i.Annotation.Defs)
            .Where(o => o.Kind == IrOperandKind.VirtualValue).Select(o => o.Name).ToHashSet();
        foreach (IrOperand? use in program.Instructions.Where(i => i.StableIdentity.EndsWith(":return-copy"))
            .SelectMany(i => i.Annotation.Uses)
            .Where(o => o.Kind == IrOperandKind.VirtualValue))
            Assert.True(definitions.Contains(use.Name), "Undefined CIL value: " + use.Name);

        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule,
            new HybridCpuBundleFormer().BundleProgram(schedule),
            resourceModel: HybridCpuMiiResourceModelV1.Create(new(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        IrProgram final = allocation.FinalSchedule.Program;
        Assert.Contains(program.Instructions, instruction =>
            instruction.StableIdentity.EndsWith(":static-load:result-copy", StringComparison.Ordinal));
        Assert.Contains(final.Instructions, instruction =>
            instruction.Annotation.ControlFlowKind == IrControlFlowKind.Call &&
            instruction.Annotation.BranchTargetSymbolName is "__hybridcpu_managed_static_load_ref" or
                "__hybridcpu_managed_static_load_i4");
        Assert.DoesNotContain(final.Instructions.SelectMany(static instruction =>
            instruction.Annotation.Defs.Concat(instruction.Annotation.Uses)),
            static operand => operand.Kind == IrOperandKind.VirtualValue);
    }
}

public static class StaticLoadResultFixture
{
    static StaticLoadResultFixture() { Number = 17; }
    public static object? Service;
    public static int Number;
    public static object? Reference(bool select)
    {
        object? value = Service;
        if (select) return value;
        return null;
    }
    public static int Integer(bool select)
    {
        int value = Number;
        if (select) return value;
        return -1;
    }
}
