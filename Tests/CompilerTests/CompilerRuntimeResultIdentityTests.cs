using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.IR.Resources;
using HybridCPU.ManagedRuntime;
using HybridCPU.Platform.Contracts;
using Xunit;

namespace HybridCPU_ISE.Tests.CompilerTests;

public sealed class CompilerRuntimeResultIdentityTests
{
    [Theory]
    [InlineData(nameof(RuntimeResultFixture.ArrayInt))]
    [InlineData(nameof(RuntimeResultFixture.ArrayByte))]
    [InlineData(nameof(RuntimeResultFixture.ArrayRef))]
    [InlineData(nameof(RuntimeResultFixture.TypeTest))]
    [InlineData(nameof(RuntimeResultFixture.Cast))]
    [InlineData(nameof(RuntimeResultFixture.Allocate))]
    [InlineData(nameof(RuntimeResultFixture.Empty))]
    [InlineData(nameof(RuntimeResultFixture.Length))]
    [InlineData(nameof(RuntimeResultFixture.Divide))]
    [InlineData(nameof(RuntimeResultFixture.Remainder))]
    [InlineData(nameof(RuntimeResultFixture.DivideUnsigned))]
    [InlineData(nameof(RuntimeResultFixture.DivideLong))]
    public void HelperResultAcrossBlock_HasCanonicalDefinition(string name)
    {
        Type type = typeof(RuntimeResultFixture);
        RestrictedCilArrayTypeBindingV1[] arrays = [];
        if (name is nameof(RuntimeResultFixture.Allocate) or nameof(RuntimeResultFixture.Empty))
        {
            HybridCpuManagedTypeSystemBuildV1 built = new HybridCpuManagedTypeSystemBuilderV1().Build([
                new("System.Byte[]", HybridCpuManagedTypeKindV1.SzArray, null, [], [],
                    new(HybridCpuManagedStorageKindV1.Primitive, null, 1, 1, 16, 24, true, false))]);
            Assert.True(built.IsSuccess, built.Reason);
            HybridCpuManagedTypeDescriptorV1 descriptor = Assert.Single(built.TypeSystem!.Descriptors);
            byte[] il = type.GetMethod(nameof(RuntimeResultFixture.Allocate))!.GetMethodBody()!.GetILAsByteArray()!;
            int token = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(Array.IndexOf(il, (byte)0x8d) + 1, 4));
            arrays = [new(token, descriptor, built.TypeSystem.TypeHandle(descriptor.TypeId)!.Value)];
        }
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2, arrayBindings: arrays)
            .ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,
                File.ReadAllBytes(type.Assembly.Location), "runtime-result-fixture",
                [new(type.FullName!, name)], []));
        Assert.True(graph.Status == RestrictedCilImportStatusV1.Success,
            string.Join(" | ", graph.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        ManagedCompiledMethodV1 method = graph.Methods.Single(m => m.Identity.MethodName == name);
        Assert.True(method.Import.ControlFlowAnalysis!.Blocks.Count > 1);
        IrProgram program = method.Import.Program!;
        HashSet<string> definitions = program.Instructions.SelectMany(i => i.Annotation.Defs)
            .Where(o => o.Kind == IrOperandKind.VirtualValue).Select(o => o.Name).ToHashSet();
        foreach (IrOperand? use in program.Instructions.SelectMany(i => i.Annotation.Uses)
            .Where(o => o.Kind == IrOperandKind.VirtualValue && o.Name is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(o.Name, @":il_[0-9a-f]+:value$")))
            Assert.True(definitions.Contains(use.Name), "Undefined CIL helper result: " + use.Name);
        IrProgramSchedule schedule = new HybridCpuLocalListScheduler().ScheduleProgram(program);
        IrRegisterAllocationResultV1 allocation = new HybridCpuScheduleAwareRegisterAllocatorV1().Allocate(schedule,
            new HybridCpuBundleFormer().BundleProgram(schedule),
            resourceModel: HybridCpuMiiResourceModelV1.Create(new(64, 64, 4, 8, 2, 16), 8),
            options: HybridCpuRegisterAllocationOptionsV1.Qualification);
        Assert.Equal(IrRegisterAllocationStatusV1.Allocated, allocation.Status);
        Assert.DoesNotContain(allocation.FinalSchedule.Program.Instructions.SelectMany(i => i.Annotation.Uses),
            o => o.Kind == IrOperandKind.VirtualValue);
    }
}

public sealed class RuntimeResultObject { }
public static class RuntimeResultFixture
{
    public static int ArrayInt(int[] input, bool select) { int value = input[0]; if (select) return value; return -1; }
    public static int ArrayByte(byte[] input, bool select) { int value = input[0]; if (select) return value; return -1; }
    public static object? ArrayRef(object[] input, bool select) { object value = input[0]; if (select) return value; return null; }
    public static object? TypeTest(object input, bool select) { RuntimeResultObject? value = input as RuntimeResultObject; if (select) return value; return null; }
    public static object? Cast(object input, bool select) { RuntimeResultObject value = (RuntimeResultObject)input; if (select) return value; return null; }
    public static byte[]? Allocate(int count, bool select) { byte[] value = new byte[count]; if (select) return value; return null; }
    public static byte[]? Empty(bool select) { byte[] value = Array.Empty<byte>(); if (select) return value; return null; }
    public static int Length(string input, bool select) { int value = input.Length; if (select) return value; return -1; }
    public static int Divide(int left, int right, bool select) { int value = left / right; if (select) return value; return -1; }
    public static int Remainder(int left, int right, bool select) { int value = left % right; if (select) return value; return -1; }
    public static uint DivideUnsigned(uint left, uint right, bool select)
    {
        uint value = left / right;
        uint fallback = value;
        if (select) return value;
        return fallback;
    }
    public static long DivideLong(long left, long right, bool select) { long value = left / right; if (select) return value; return -1; }
}
